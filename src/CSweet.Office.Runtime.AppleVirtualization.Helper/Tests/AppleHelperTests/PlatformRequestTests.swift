import Foundation
import XCTest
@testable import CSweetAppleVirtualizationHelper

final class PlatformRequestTests: XCTestCase {
    private let workload: [String: Any] = [
        "workloadId": "00000000-0000-0000-0000-000000000001", "kind": 2,
        "resourceLimits": ["virtualCpuCount": 1, "cpuPercent": 100, "memoryMegabytes": 1024,
                           "writableDiskMegabytes": 64, "maximumProcessCount": 64, "maximumLogBytes": 1024],
        "brokerLease": ["expiresAt": "2026-11-23T12:00:00Z"],
        "adapterArtifact": ["digest": "sha256:" + String(repeating: "a", count: 64)]
    ]

    private func request(_ keys: [String]) throws -> PlatformRequest {
        let fields = Dictionary(uniqueKeysWithValues: keys.map { ($0, workload) })
        return try JSONDecoder.csweet.decode(PlatformRequest.self, from: JSONSerialization.data(withJSONObject: fields))
    }

    func testProtectedChildPathIsStableAcrossCreation() throws {
        let root = "/private/tmp/csweet-helper-test-" + UUID().uuidString
        try FileManager.default.createDirectory(atPath: root, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(atPath: root) }
        let candidate = try safeChild(root, "scratch.raw")
        try Data().write(to: URL(fileURLWithPath: candidate))
        XCTAssertEqual(try safeChild(root, "scratch.raw"), candidate)
        XCTAssertEqual(try canonicalFile(candidate), root + "/scratch.raw")
        XCTAssertEqual(try canonicalFile(root.replacingOccurrences(of: "/private/tmp/", with: "/tmp/")), root)
        XCTAssertThrowsError(try safeChild(root, "../scratch.raw"))
    }

    func testToolchainAdapterArtifactDecodes() throws {
        let selected = try XCTUnwrap(request(["toolchainBuildWorkload"]).singleWorkload)
        XCTAssertEqual(selected.adapterArtifact?.digest, "sha256:" + String(repeating: "a", count: 64))
    }

    func testRejectsAmbiguousAndUnsupportedRequests() throws {
        XCTAssertNil(try request(["runtimeWorkload", "toolchainBuildWorkload"]).singleWorkload)
        XCTAssertNil(try request(["builderWorkload", "computeEnvironment"]).singleWorkload)
        XCTAssertNotNil(try request(["computeEnvironment"]).singleWorkload)
    }
}
