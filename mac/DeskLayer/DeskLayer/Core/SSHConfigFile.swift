//
//  SSHConfigFile.swift
//  DeskLayer
//
//  Reads host aliases from ~/.ssh/config so the inspector can offer them.
//  Using an alias means ssh resolves the real hostname, user, port, and
//  identity file itself — usually the least you have to type.
//

import Foundation

nonisolated enum SSHConfigFile {
    /// Alias names from ~/.ssh/config, in file order, excluding patterns
    /// (`*`, `?`) and negations which aren't concrete destinations.
    static func aliases(at url: URL? = nil) -> [String] {
        let path = url ?? FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".ssh/config")
        guard let text = try? String(contentsOf: path, encoding: .utf8) else { return [] }
        var names: [String] = []
        for line in text.components(separatedBy: .newlines) {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            guard !trimmed.hasPrefix("#") else { continue }
            let parts = trimmed.split(separator: " ", omittingEmptySubsequences: true)
            guard parts.count >= 2, parts[0].lowercased() == "host" else { continue }
            for token in parts.dropFirst() {
                let name = String(token)
                guard !name.contains("*"), !name.contains("?"), !name.hasPrefix("!") else { continue }
                if !names.contains(name) { names.append(name) }
            }
        }
        return names
    }
}
