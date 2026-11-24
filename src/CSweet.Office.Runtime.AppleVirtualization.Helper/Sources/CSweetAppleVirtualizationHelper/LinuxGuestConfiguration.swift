import Foundation
import Virtualization

func makeOfficeGuestConfiguration(kernel: String, guest: String, scratch: String, artifact: String?,
                                 cpus: Int, memoryMiB: Int, port: UInt32, compute: Bool = false) throws -> VZVirtualMachineConfiguration {
    for path in [kernel, guest] {
        guard FileManager.default.isReadableFile(atPath: path) else {
            throw HelperError(code: "image-unavailable", message: "An immutable virtual machine image is unavailable.")
        }
    }
    let configuration = VZVirtualMachineConfiguration()
    configuration.platform = VZGenericPlatformConfiguration()
    let bootLoader = VZLinuxBootLoader(kernelURL: URL(fileURLWithPath: kernel))
    let initrd = URL(fileURLWithPath: kernel).deletingLastPathComponent().appendingPathComponent("initrd.img")
    if FileManager.default.fileExists(atPath: initrd.path) {
        bootLoader.initialRamdiskURL = initrd
    }
    if compute && (cpus < VZVirtualMachineConfiguration.minimumAllowedCPUCount || cpus > VZVirtualMachineConfiguration.maximumAllowedCPUCount) {
        throw HelperError(code: "invalid-resources", message: "The requested CPU count exceeds host limits.")
    }
    bootLoader.commandLine = "console=hvc0 panic=-1 reboot=t root=/dev/vda \(compute ? "rw" : "ro") csweet.broker_port=\(port)"
    configuration.bootLoader = bootLoader
    // Linux boots with console=hvc0 even when the system service discards console output.
    let serial = VZVirtioConsoleDeviceSerialPortConfiguration()
    serial.attachment = VZFileHandleSerialPortAttachment(fileHandleForReading: nil,
        fileHandleForWriting: try FileHandle(forWritingTo: URL(fileURLWithPath: "/dev/null")))
    configuration.serialPorts = [serial]
    configuration.cpuCount = max(VZVirtualMachineConfiguration.minimumAllowedCPUCount,
        min(cpus, VZVirtualMachineConfiguration.maximumAllowedCPUCount))
    let memoryBytes = UInt64(memoryMiB) * 1_048_576
    guard memoryBytes >= VZVirtualMachineConfiguration.minimumAllowedMemorySize,
          memoryBytes <= VZVirtualMachineConfiguration.maximumAllowedMemorySize else {
        throw HelperError(code: "invalid-resources", message: "The requested memory is unsupported on this host.")
    }
    configuration.memorySize = memoryBytes
    configuration.entropyDevices = [VZVirtioEntropyDeviceConfiguration()]
    configuration.socketDevices = [VZVirtioSocketDeviceConfiguration()]
    configuration.networkDevices = []

    var storage: [VZStorageDeviceConfiguration] = []
    if !compute { storage.append(try blockDevice(guest, readOnly: true)) }
    storage.append(try blockDevice(scratch, readOnly: false))
    if let artifact {
        storage.append(try blockDevice(artifact, readOnly: true))
    }
    configuration.storageDevices = storage
    try configuration.validate()
    return configuration
}

private func blockDevice(_ path: String, readOnly: Bool) throws -> VZVirtioBlockDeviceConfiguration {
    let attachment = try VZDiskImageStorageDeviceAttachment(
        url: URL(fileURLWithPath: path), readOnly: readOnly,
        cachingMode: readOnly ? .cached : .automatic,
        synchronizationMode: readOnly ? .none : .full)
    return VZVirtioBlockDeviceConfiguration(attachment: attachment)
}
