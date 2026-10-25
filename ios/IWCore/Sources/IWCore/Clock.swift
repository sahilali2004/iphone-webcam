import Foundation

/// Monotonic microseconds on the same timebase as AVFoundation's host clock
/// (CMClockGetHostTimeClock / mach_absolute_time), so capture PTS and PING times are comparable.
public enum MonoClock {
    public static var nowUs: Int64 {
        Int64(clock_gettime_nsec_np(CLOCK_UPTIME_RAW) / 1_000)
    }
}

/// Address filter: loopback, RFC1918, IPv4 link-local, IPv6 link-local / ULA. Mirrors WifiTransport.IsLocal.
public enum LocalAddress {
    public static func isLocal(ipv4 b: [UInt8]) -> Bool {
        guard b.count == 4 else { return false }
        return b[0] == 127
            || b[0] == 10
            || (b[0] == 172 && (16...31).contains(b[1]))
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
    }

    public static func isLocal(ipv6 b: [UInt8]) -> Bool {
        guard b.count == 16 else { return false }
        if b[0..<15].allSatisfy({ $0 == 0 }) && b[15] == 1 { return true }                 // ::1
        if b[0..<10].allSatisfy({ $0 == 0 }) && b[10] == 0xFF && b[11] == 0xFF {             // ::ffff:a.b.c.d
            return isLocal(ipv4: Array(b[12..<16]))
        }
        return (b[0] == 0xFE && (b[1] & 0xC0) == 0x80)                                       // fe80::/10
            || (b[0] & 0xFE) == 0xFC                                                         // fc00::/7
    }
}
