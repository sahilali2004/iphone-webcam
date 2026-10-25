import Foundation
import Security

/// Paired PCs, stored in the Keychain (this device only, never synced): pcId → pairing key.
final class PairingStore {
    struct PairedPC: Identifiable, Equatable {
        let id: String
        let name: String
    }

    private let service = "local.iphonewebcam.pairing"

    func key(for pcId: String) -> Data? {
        var query = baseQuery(pcId)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess else { return nil }
        return result as? Data
    }

    func save(key: Data, pcId: String, pcName: String) {
        SecItemDelete(baseQuery(pcId) as CFDictionary)
        var item = baseQuery(pcId)
        item[kSecValueData as String] = key
        item[kSecAttrLabel as String] = pcName
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        SecItemAdd(item as CFDictionary, nil)
    }

    func remove(pcId: String) {
        SecItemDelete(baseQuery(pcId) as CFDictionary)
    }

    func all() -> [PairedPC] {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecReturnAttributes as String: true,
            kSecMatchLimit as String: kSecMatchLimitAll,
        ]
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let items = result as? [[String: Any]] else { return [] }
        return items.compactMap { item in
            guard let id = item[kSecAttrAccount as String] as? String else { return nil }
            return PairedPC(id: id, name: item[kSecAttrLabel as String] as? String ?? "PC")
        }
    }

    private func baseQuery(_ pcId: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: pcId,
        ]
    }
}

/// Random per-install identifier advertised to PCs. Not a hardware ID.
enum DeviceIdentity {
    static let deviceId: String = {
        let key = "deviceId"
        if let existing = UserDefaults.standard.string(forKey: key) { return existing }
        let id = UUID().uuidString
        UserDefaults.standard.set(id, forKey: key)
        return id
    }()

    /// Hardware model identifier, e.g. "iPhone15,2".
    static let model: String = {
        var info = utsname()
        uname(&info)
        return withUnsafeBytes(of: &info.machine) { raw in
            String(decoding: raw.prefix(while: { $0 != 0 }), as: UTF8.self)
        }
    }()
}
