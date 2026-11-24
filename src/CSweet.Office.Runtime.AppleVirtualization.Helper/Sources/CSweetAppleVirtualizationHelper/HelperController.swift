import Darwin
import Foundation
import Virtualization

@available(macOS 14.0, *)
final class HelperController {
    private let paths: HelperPaths

    init(paths: HelperPaths) {
        self.paths = paths
    }

    func execute(_ operation: String, request: PlatformRequest) -> PlatformResponse {
        do {
            if let key = request.attemptKey {
                return try computeAttempt(operation, request: request, key: key)
            }
            if request.computeEnvironment != nil || request.handle?.kind == 3 && operation == "destroy" {
                throw HelperError(code: "attempt-required", message: "Compute creation and removal require exact attempt authority.")
            }
            switch operation {
            case "probe": return try probe()
            case "create": return try create(request)
            case "start", "inspect", "stop", "destroy": return try lifecycle(operation, request: request)
            case "logs": return try logs(request)
            case "reap": return try reap()
            default: throw HelperError(code: "unsupported-operation", message: "The requested helper operation is unsupported.")
            }
        } catch let error as HelperError {
            return .failure(error.code, error.message)
        } catch {
            return .failure("helper-operation-failed", "The Apple Virtualization operation failed.")
        }
    }

    func openGuestChannel(_ request: PlatformRequest) throws -> Int32 {
        let loaded = try load(request.handle)
        let descriptor = try connectUnixSocket(path: loaded.metadata.managerSocketPath)
        do {
            try sendManagerRequest("open-guest-channel", metadata: loaded.metadata, descriptor: descriptor)
            return descriptor
        } catch {
            close(descriptor)
            throw error
        }
    }

    private func probe() throws -> PlatformResponse {
        guard VZVirtualMachine.isSupported else {
            throw HelperError(code: "unsupported-host", message: "Apple Virtualization is unavailable on this Mac.")
        }
        guard geteuid() == 0 else {
            throw HelperError(code: "privilege-required", message: "RuntimeHost must run as the protected system service.")
        }
        try ensureProtectedDirectory(paths.dataRoot)
        try ensureProtectedDirectory(paths.instancesRoot)
        try ensureProtectedDirectory(paths.managerSocketsRoot)
        guard trustedRegularFile(paths.kernelImagePath, executable: false) else {
            throw HelperError(code: "kernel-not-installed", message: "The pinned Apple Virtualization guest kernel is unavailable.")
        }
        return .ok(transport: true)
    }

    private func create(_ request: PlatformRequest, computeId: UUID? = nil) throws -> PlatformResponse {
        _ = try probe()
        guard let workload = request.singleWorkload else {
            throw HelperError(code: "invalid-workload", message: "Exactly one typed workload must be supplied.")
        }
        guard (computeId != nil) == (workload.kind == 3) else {
            throw HelperError(code: "invalid-workload", message: "The workload kind does not match the request.")
        }
        try workload.resourceLimits.validate()
        guard let guestValue = request.guestImagePath,
              trustedRegularFile(guestValue, executable: false),
              ["img", "raw"].contains(URL(fileURLWithPath: guestValue).pathExtension.lowercased()) else {
            throw HelperError(code: "invalid-guest-image", message: "The immutable Apple Virtualization guest image is invalid.")
        }
        let guestImage = try canonicalFile(guestValue)
        var artifactImage: String?
        if request.runtimeWorkload != nil || request.toolchainBuildWorkload != nil {
            guard let artifact = request.runtimeWorkload?.artifact ?? request.toolchainBuildWorkload?.adapterArtifact else {
                throw HelperError(code: "invalid-artifact-media", message: "The workload artifact reference is missing.")
            }
            guard let value = request.artifactImagePath,
                  let resolved = try? canonicalFile(value),
                  URL(fileURLWithPath: resolved).deletingLastPathComponent().path == (try? canonicalFile(paths.artifactMediaRoot)),
                  URL(fileURLWithPath: resolved).lastPathComponent == String(artifact.digest.dropFirst(7)) + ".iso",
                  verifyArtifactISO(path: resolved, expectedDigest: artifact.digest) else {
                throw HelperError(code: "invalid-artifact-media", message: "The runtime artifact media failed its path or integrity check.")
            }
            artifactImage = resolved
        } else if request.artifactImagePath != nil {
            throw HelperError(code: "invalid-artifact-media", message: "Builder workloads cannot attach runtime artifact media.")
        }

        let instanceId = computeId ?? UUID()
        let directory = try paths.instanceDirectory(instanceId)
        try ensureProtectedDirectory(directory)
        let scratch = try safeChild(directory, "scratch.raw")
        let metadataPath = try safeChild(directory, "instance.json")
        let socketPath = try safeChild(paths.managerSocketsRoot,
            instanceId.uuidString.replacingOccurrences(of: "-", with: "").lowercased() + ".sock")
        do {
            if computeId != nil {
                let size = off_t(workload.resourceLimits.writableDiskMegabytes) * 1_048_576
                let original = try FileManager.default.attributesOfItem(atPath: guestImage)[.size] as? NSNumber
                guard let original, original.int64Value <= size else {
                    throw HelperError(code: "invalid-resources", message: "The Compute template exceeds the disk quota.")
                }
                try FileManager.default.copyItem(atPath: guestImage, toPath: scratch)
                guard chmod(scratch, 0o600) == 0, truncate(scratch, size) == 0 else {
                    throw HelperError(code: "disk-create-failed", message: "The private Compute disk could not be sized.")
                }
            } else {
                try createScratch(scratch, megabytes: workload.resourceLimits.writableDiskMegabytes)
            }
            let metadata = InstanceMetadata(
                instanceId: instanceId, workloadId: workload.workloadId, kind: workload.kind,
                managerPid: 0, managerSocketPath: socketPath, managerToken: try secureRandomToken(),
                guestImagePath: guestImage, artifactImagePath: artifactImage,
                kernelImagePath: try canonicalFile(paths.kernelImagePath), scratchImagePath: scratch,
                resources: workload.resourceLimits, brokerPort: computeId == nil ? paths.brokerPort : 2763,
                createdAt: Date(), startedAt: nil, finishedAt: nil,
                leaseExpiresAt: request.runtimeWorkload == nil ? nil : workload.brokerLease.expiresAt)
            try saveMetadata(metadata, to: metadataPath)
            try launchManager(metadataPath: metadataPath, socket: socketPath)
            return PlatformResponse(success: true, providerInstanceId: metadata.handle.providerInstanceId)
        } catch {
            if computeId == nil { try? FileManager.default.removeItem(atPath: directory) }
            throw error
        }
    }

    // The signed partition uses the environment UUID as its creation key. Keep a
    // durable tombstone even after removing the disk so delayed creates cannot replay.
    private func computeAttempt(_ operation: String, request: PlatformRequest, key: UUID) throws -> PlatformResponse {
        guard geteuid() == 0, key != UUID(uuidString: "00000000-0000-0000-0000-000000000000"),
              request.handle == nil else { throw HelperError(code: "invalid-attempt", message: "Invalid Compute attempt.") }
        let root = try safeChild(paths.dataRoot, "compute-attempts")
        try ensureProtectedDirectory(root)
        let id = key.uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        let path = try safeChild(root, id)
        let lock = open(path + ".lock", O_CREAT | O_RDWR | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard lock >= 0 else { throw HelperError(code: "attempt-lock-failed", message: "Cannot lock the Compute attempt.") }
        defer { close(lock) }
        guard flock(lock, LOCK_EX) == 0 else { throw HelperError(code: "attempt-lock-failed", message: "Cannot lock the Compute attempt.") }
        let phase = FileManager.default.fileExists(atPath: path) ? try String(contentsOfFile: path, encoding: .utf8) : nil
        func save(_ value: String) throws { try value.write(toFile: path, atomically: true, encoding: .utf8) }
        if operation == "create" {
            guard phase == nil, request.computeEnvironment?.workloadId == key, request.computeEnvironment?.kind == 3,
                  request.singleWorkload != nil else {
                throw HelperError(code: "invalid-attempt", message: "The Compute attempt is invalid or already recorded.")
            }
            try save("preparing")
            let result = try create(request, computeId: key)
            try save("created")
            return result
        }
        guard request.recoveryWorkloadKind == 3, request.recoveryWorkloadId == key else {
            throw HelperError(code: "invalid-attempt", message: "Compute recovery identity does not match.")
        }
        let handle = WorkloadHandle(providerId: providerID, workloadId: key, providerInstanceId: id, kind: 3)
        let directory = try paths.instanceDirectory(key)
        switch operation {
        case "inspect":
            guard phase == "created" || phase == "preparing" else { return .failure("creation-unconfirmed", "Compute creation is unconfirmed.") }
            guard let loaded = try? load(handle) else { return .failure("creation-unconfirmed", "Compute creation is unconfirmed.") }
            if phase == "preparing" {
                guard (try? managerResponse("inspect", metadata: loaded.metadata))?.success == true else {
                    return .failure("creation-unconfirmed", "Compute manager creation is unconfirmed.")
                }
                try save("created")
            }
            return PlatformResponse(success: true, providerInstanceId: id)
        case "inspect-destruction":
            guard phase == "retired" || phase == "never-created" else { return .failure("destruction-unconfirmed", "Compute removal is unconfirmed.") }
            return PlatformResponse(success: true, providerInstanceId: phase == "retired" ? id : nil)
        case "destroy":
            if phase == nil || phase == "never-created" {
                guard !FileManager.default.fileExists(atPath: directory) else { return .failure("destruction-unconfirmed", "Unexpected instance data exists.") }
                try save("never-created")
                return .ok()
            }
            if phase != "retired" {
                if FileManager.default.fileExists(atPath: directory) {
                    let socket = try safeChild(paths.managerSocketsRoot, id + ".sock")
                    if let loaded = try? load(handle) {
                        // A responsive manager must confirm shutdown. If it has already
                        // exited, its kernel lock independently proves the disk is unused.
                        if let response = try? managerResponse("destroy", metadata: loaded.metadata, grace: 0),
                           !response.success { return response }
                    } else if phase != "preparing" {
                        return .failure("destruction-unconfirmed", "Compute metadata is unavailable.")
                    }
                    var ownership: FileHandle?
                    for _ in 0..<100 {
                        ownership = try? lockComputeManager(directory)
                        if ownership != nil { break }
                        Thread.sleep(forTimeInterval: 0.02)
                    }
                    guard let ownership else { return .failure("destruction-unconfirmed", "Compute manager still owns the disk.") }
                    try withExtendedLifetime(ownership) {
                        try FileManager.default.removeItem(atPath: directory)
                        unlink(socket)
                    }
                }
                try save("retired")
            }
            return PlatformResponse(success: true, providerInstanceId: id)
        default: throw HelperError(code: "invalid-attempt", message: "Unsupported Compute recovery operation.")
        }
    }

    private func lifecycle(_ operation: String, request: PlatformRequest) throws -> PlatformResponse {
        let loaded: (metadata: InstanceMetadata, directory: String)
        do { loaded = try load(request.handle) }
        catch let error as HelperError where error.code == "not-found" {
            return operation == "inspect" ? .failure("not-found", "The workload was not found.") : .ok()
        }
        let response: PlatformResponse
        do { response = try managerResponse(operation, metadata: loaded.metadata, grace: request.gracePeriodSeconds) }
        catch where loaded.metadata.kind == 3 && ["start", "stop", "inspect"].contains(operation) {
            // Only an unowned private disk may acquire a replacement manager.
            let ownership = try lockComputeManager(loaded.directory)
            unlink(loaded.metadata.managerSocketPath)
            try ownership.close()
            try launchManager(metadataPath: safeChild(loaded.directory, "instance.json"), socket: loaded.metadata.managerSocketPath)
            response = try managerResponse(operation, metadata: loaded.metadata, grace: request.gracePeriodSeconds)
        }
        if operation == "destroy", response.success {
            unlink(loaded.metadata.managerSocketPath)
            try? FileManager.default.removeItem(atPath: loaded.directory)
        }
        return response
    }

    private func logs(_ request: PlatformRequest) throws -> PlatformResponse {
        guard request.handle != nil, let maximum = request.maximumBytes,
              maximum > 0 && maximum <= 1_048_576 else {
            throw HelperError(code: "invalid-request", message: "The bounded log request is invalid.")
        }
        _ = try load(request.handle)
        return PlatformResponse(success: true, logs: [])
    }

    private func reap() throws -> PlatformResponse {
        guard FileManager.default.fileExists(atPath: paths.instancesRoot) else {
            return PlatformResponse(success: true, workloadsRemoved: 0)
        }
        let now = Date()
        var removed = 0
        for name in try FileManager.default.contentsOfDirectory(atPath: paths.instancesRoot).prefix(10_000) {
            guard name.range(of: #"^[0-9a-f]{32}$"#, options: .regularExpression) != nil,
                  let directory = try? safeChild(paths.instancesRoot, name),
                  let metadata = try? loadMetadata(from: try safeChild(directory, "instance.json")),
                  metadata.kind == 1,
                  metadata.finishedAt != nil || metadata.leaseExpiresAt == nil || metadata.leaseExpiresAt! <= now else { continue }
            _ = try? managerResponse("destroy", metadata: metadata, grace: 0)
            try? FileManager.default.removeItem(atPath: directory)
            unlink(metadata.managerSocketPath)
            removed += 1
        }
        return PlatformResponse(success: true, workloadsRemoved: removed)
    }

    private func load(_ handle: WorkloadHandle?) throws -> (metadata: InstanceMetadata, directory: String) {
        guard let handle, handle.providerId == providerID,
              handle.workloadId != UUID(uuidString: "00000000-0000-0000-0000-000000000000")!,
              handle.providerInstanceId.range(of: #"^[0-9a-f]{32}$"#, options: .regularExpression) != nil else {
            throw HelperError(code: "invalid-handle", message: "The workload handle is invalid.")
        }
        let directory = try safeChild(paths.instancesRoot, handle.providerInstanceId)
        let metadataPath = try safeChild(directory, "instance.json")
        guard let metadata = try? loadMetadata(from: metadataPath), metadata.handle == handle,
              metadata.managerSocketPath == (try safeChild(paths.managerSocketsRoot, handle.providerInstanceId + ".sock")),
              metadata.scratchImagePath == (try safeChild(directory, "scratch.raw")) else {
            throw HelperError(code: "not-found", message: "The workload was not found.")
        }
        return (metadata, directory)
    }

    private func managerResponse(_ operation: String, metadata: InstanceMetadata, grace: Int? = nil) throws -> PlatformResponse {
        let descriptor = try connectUnixSocket(path: metadata.managerSocketPath)
        defer { close(descriptor) }
        try sendManagerRequest(operation, metadata: metadata, grace: grace, descriptor: descriptor)
        let data = try readSocketLine(descriptor)
        return try JSONDecoder.csweet.decode(PlatformResponse.self, from: data)
    }

    private func sendManagerRequest(
        _ operation: String, metadata: InstanceMetadata, grace: Int? = nil, descriptor: Int32) throws {
        var data = try JSONEncoder.csweet.encode(ManagerRequest(
            token: metadata.managerToken, operation: operation, gracePeriodSeconds: grace))
        data.append(0x0a)
        try sendAll(descriptor, data)
    }

    private func launchManager(metadataPath: String, socket: String) throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: try helperExecutable())
        process.arguments = ["--workload-host", metadataPath]
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        try waitForManager(socket, timeout: 10)
    }

    private func waitForManager(_ path: String, timeout: TimeInterval) throws {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if let descriptor = try? connectUnixSocket(path: path) {
                close(descriptor)
                return
            }
            Thread.sleep(forTimeInterval: 0.025)
        }
        throw HelperError(code: "manager-start-timeout", message: "The workload manager did not start in time.")
    }

    private func createScratch(_ path: String, megabytes: Int) throws {
        let descriptor = open(path, O_RDWR | O_CREAT | O_EXCL | O_NOFOLLOW, S_IRUSR | S_IWUSR)
        guard descriptor >= 0 else {
            throw HelperError(code: "scratch-create-failed", message: "The bounded scratch device could not be created.")
        }
        defer { close(descriptor) }
        guard ftruncate(descriptor, off_t(megabytes) * 1_048_576) == 0 else {
            throw HelperError(code: "scratch-create-failed", message: "The bounded scratch device could not be sized.")
        }
    }

    private func helperExecutable() throws -> String {
        try canonicalFile(CommandLine.arguments[0])
    }

    private func trustedRegularFile(_ path: String, executable: Bool) -> Bool {
        var status = stat()
        guard path.hasPrefix("/"), lstat(path, &status) == 0,
              status.st_mode & S_IFMT == S_IFREG,
              status.st_mode & (S_IWGRP | S_IWOTH) == 0 else { return false }
        return !executable || status.st_mode & S_IXUSR != 0
    }
}
