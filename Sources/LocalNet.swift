import Foundation
import Darwin

/// 本机局域网地址：告诉对端往哪发，以及发现信标该往哪些广播地址发。
enum LocalNet {
    struct Address {
        let name: String
        let ip: String
        let broadcast: String?
        let isLAN: Bool
    }

    static func addresses() -> [Address] {
        var result: [Address] = []
        var ifaddr: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddr) == 0, let first = ifaddr else { return [] }
        defer { freeifaddrs(ifaddr) }

        var ptr: UnsafeMutablePointer<ifaddrs>? = first
        while let p = ptr {
            defer { ptr = p.pointee.ifa_next }
            guard let sa = p.pointee.ifa_addr, sa.pointee.sa_family == UInt8(AF_INET) else { continue }
            let flags = Int32(p.pointee.ifa_flags)
            guard flags & IFF_UP != 0, flags & IFF_LOOPBACK == 0 else { continue }
            let name = String(cString: p.pointee.ifa_name)
            let ip = Sock.ip(sa.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee })
            guard !ip.hasPrefix("169.254.") else { continue }
            var bcast: String?
            if flags & IFF_BROADCAST != 0, let d = p.pointee.ifa_dstaddr,
               d.pointee.sa_family == UInt8(AF_INET) {
                bcast = Sock.ip(d.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee })
            }
            result.append(Address(name: name, ip: ip, broadcast: bcast, isLAN: name.hasPrefix("en")))
        }
        // Wi-Fi/以太网优先，虚拟网卡（utun、bridge、awdl 之类）放后面。
        return result.sorted { a, b in
            if a.isLAN != b.isLAN { return a.isLAN }
            return a.name < b.name
        }
    }

    static func primaryIPv4() -> String? {
        addresses().first?.ip
    }

    static func broadcastAddresses() -> [String] {
        var set = Set(addresses().compactMap { $0.broadcast })
        set.insert("255.255.255.255")
        return Array(set)
    }
}
