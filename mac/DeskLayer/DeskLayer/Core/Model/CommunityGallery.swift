//
//  CommunityGallery.swift
//  DeskLayer
//
//  Paged, sortable browse of everything published to the community store —
//  GET /api/store/plugins. Read-only and unauthenticated; the classic
//  catalog.json path is untouched.
//

import Combine
import Foundation
import os


/// The server's timestamps carry fractional seconds ("…T16:03:07.763Z"),
/// which ISO8601DateFormatter refuses by default.
nonisolated enum StoreDates {
    static func parse(_ text: String) -> Date? {
        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = fractional.date(from: text) { return date }
        return ISO8601DateFormatter().date(from: text)
    }
}

/// One published plugin, as the gallery endpoint lists it. Richer than the
/// catalog's StorePlugin: downloads and publishedAt exist only here.
nonisolated struct GalleryPlugin: Codable, Hashable, Identifiable {
    var name: String
    var slug: String
    var description: String?
    /// Direct download for the current version's .js.
    var url: String
    var version: String?
    var author: String?
    /// Absolute preview image URL; absent when the publisher sent none.
    /// Detail-view sized (can be 2MB) — grids must use `thumbnail`.
    var preview: String?
    /// Small (~480px) grid image; loaded by the gallery tiles.
    var thumbnail: String?
    var cheers: Int?
    var comments: Int?
    var downloads: Int?
    var verified: Bool?
    var topicUrl: String?
    /// ISO 8601, latest version's publish time.
    var publishedAt: String?
    /// Automated security review: "pending" | "checked" | "blocked"; absent
    /// on entries that predate the reviewer. Advisory — staff `verified`
    /// stays the strong signal, and the UI keeps saying so.
    var aiReview: String?
    var aiReviewNote: String?

    var id: String { slug }

    var publishedDate: Date? {
        publishedAt.flatMap(StoreDates.parse)
    }

    /// The gallery entry in the catalog's shape, so the existing install
    /// path (download, validate, record origin) is reused as-is.
    var asStorePlugin: StorePlugin {
        StorePlugin(name: name, description: description, preview: preview,
                    url: url, version: version, author: author,
                    cheers: cheers, comments: comments,
                    verified: verified, topicUrl: topicUrl)
    }
}

nonisolated enum GallerySort: String, CaseIterable, Identifiable {
    case cheers
    case downloads
    case latest

    var id: String { rawValue }

    var title: String {
        switch self {
        case .cheers: return String(localized: "Top Cheered")
        case .downloads: return String(localized: "Most Downloaded")
        case .latest: return String(localized: "Latest")
        }
    }
}

@MainActor
final class CommunityGallery: ObservableObject {
    @Published private(set) var plugins: [GalleryPlugin] = []
    @Published private(set) var page = 1
    @Published private(set) var pages = 1
    @Published private(set) var total = 0
    @Published private(set) var isLoading = false
    @Published private(set) var error: String?
    @Published var sort: GallerySort = .cheers {
        didSet { if sort != oldValue { load(page: 1) } }
    }
    /// Case-insensitive substring over name + description + author.
    /// Applied on submit, not per keystroke — see the view.
    @Published var query = ""
    @Published var verifiedOnly = false {
        didSet { if verifiedOnly != oldValue { load(page: 1) } }
    }

    static let pageSize = 24

    private var task: Task<Void, Never>?
    private let log = Logger(subsystem: "com.qiudaomao.DeskLayer", category: "gallery")

    private let session: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 20
        config.urlCache = nil
        return URLSession(configuration: config)
    }()

    private struct Page: Decodable {
        var plugins: [GalleryPlugin]
        var page: Int?
        var pages: Int?
        var total: Int?
    }

    func load(page requested: Int = 1) {
        task?.cancel()
        isLoading = true
        error = nil
        var components = URLComponents(string: "https://store.byteplayer.app/api/store/plugins")!
        var items = [
            URLQueryItem(name: "sort", value: sort.rawValue),
            URLQueryItem(name: "page", value: String(requested)),
            URLQueryItem(name: "limit", value: String(Self.pageSize)),
        ]
        let trimmed = query.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmed.isEmpty { items.append(URLQueryItem(name: "q", value: trimmed)) }
        if verifiedOnly { items.append(URLQueryItem(name: "verified", value: "true")) }
        components.queryItems = items
        let url = components.url!
        task = Task { [weak self] in
            defer { self?.isLoading = false }
            do {
                guard let self else { return }
                let (data, response) = try await self.session.data(from: url)
                try Task.checkCancellation()
                if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
                    self.error = "HTTP \(http.statusCode)"
                    return
                }
                let decoded = try JSONDecoder().decode(Page.self, from: data)
                self.plugins = decoded.plugins
                self.page = decoded.page ?? requested
                self.pages = max(decoded.pages ?? 1, 1)
                self.total = decoded.total ?? decoded.plugins.count
            } catch is CancellationError {
                // Superseded by a newer load; the newer one owns the state.
            } catch {
                self?.error = error.localizedDescription
                self?.log.error("gallery load failed: \(error.localizedDescription, privacy: .public)")
            }
        }
    }
}
