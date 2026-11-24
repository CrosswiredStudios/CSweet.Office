import Darwin
import Foundation
import Virtualization

@main
enum GuestBootProbe {
    static func main() throws {
        let args = CommandLine.arguments
        guard args.count == 4 else { exit(2) }
        let config = try makeOfficeGuestConfiguration(kernel: args[1], guest: args[2], scratch: args[3],
            artifact: nil, cpus: 2, memoryMiB: 1024, port: 5000)
        let vm = VZVirtualMachine(configuration: config)
        let timer = DispatchSource.makeTimerSource(queue: .main)
        let deadline = Date().addingTimeInterval(110)
        var connecting = false
        timer.setEventHandler {
            guard Date() < deadline else { fputs("Guest broker deadline exceeded\n", stderr); exit(1) }
            guard !connecting, vm.state == .running, let socket = vm.socketDevices.first as? VZVirtioSocketDevice else { return }
            connecting = true
            socket.connect(toPort: 5000) { result in
                connecting = false
                if case .success(let channel) = result {
                    timer.cancel()
                    print("Office Linux guest booted with zero network devices. Connected to guest broker on virtio socket port 5000 (fd \(channel.fileDescriptor)).")
                    vm.stop { error in
                        if let error { fputs("VM stop failed: \(error)\n", stderr); exit(1) }
                        print("VM stopped.")
                        exit(0)
                    }
                }
            }
        }
        timer.schedule(deadline: .now(), repeating: 1)
        timer.resume()
        vm.start { result in
            switch result {
            case .failure(let error): fputs("VM start failed: \(error)\n", stderr); exit(1)
            case .success: break
            }
        }
        RunLoop.main.run()
    }
}
