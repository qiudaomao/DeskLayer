//
//  JSUIPrelude.swift
//  DeskLayer
//
//  Loads the pure-JS view builders injected into every plugin context. The
//  JS itself lives in shared/runtime/prelude.js — the canonical copy shared
//  with other platform ports — bundled here as Resources/prelude.js and kept
//  identical by scripts/check-docs-sync.sh. See that file for the builder
//  and action-table contract.
//

import Foundation

nonisolated enum JSUIPrelude {
    /// The prelude source, read once from the bundle. An empty string means
    /// the resource was dropped from the build — declarative plugins would
    /// silently render nothing, so tests assert this is non-empty.
    static let source: String = {
        guard let url = Bundle(for: BundleToken.self).url(forResource: "prelude", withExtension: "js"),
              let text = try? String(contentsOf: url, encoding: .utf8) else {
            assertionFailure("prelude.js is missing from the app bundle")
            return ""
        }
        return text
    }()
}

private final class BundleToken {}
