//
//  KeychainStore.swift
//  DeskLayer
//
//  Secrets (SSH passwords) never touch the hand-editable layout.json —
//  they live in the login keychain, keyed by the layout item's id.
//

import Foundation
import Security

nonisolated enum KeychainStore {
    private static let service = "com.qiudaomao.DeskLayer.ssh"

    /// One password per (item, SSH host config).
    private static func account(_ id: UUID, _ hostID: UUID?) -> String {
        guard let hostID else { return id.uuidString }
        return "\(id.uuidString)/\(hostID.uuidString)"
    }

    static func setPassword(_ password: String?, forItem id: UUID, host hostID: UUID? = nil) {
        let account = account(id, hostID)
        // Clear any existing entry first (also the delete-then-set path).
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(query as CFDictionary)

        guard let password, !password.isEmpty, let data = password.data(using: .utf8) else { return }
        var add = query
        add[kSecValueData as String] = data
        add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
        SecItemAdd(add as CFDictionary, nil)
    }

    static func password(forItem id: UUID, host hostID: UUID? = nil) -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account(id, hostID),
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func hasPassword(forItem id: UUID, host hostID: UUID? = nil) -> Bool {
        password(forItem: id, host: hostID) != nil
    }
}
