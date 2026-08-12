//
//  CommunityAccount.swift
//  DeskLayer
//
//  Sign-in and publishing against the community store
//  (store.byteplayer.app). Accounts live on the forum; the store backend
//  bridges them with a device-code flow, so the app never sees a password
//  and needs no URL scheme: open a browser page, poll for the token.
//
//  The bearer token is long-lived and delivered exactly once — it goes
//  straight to the login keychain, same rule as SSH passwords and API keys.
//

import AppKit
import Combine
import Foundation
import os

/// The signed-in forum user, as /auth/token and /api/me return it.
nonisolated struct CommunityUser: Codable, Equatable {
    var username: String
    var name: String?
}

/// What a publish attempt produced.
nonisolated enum PublishResult: Equatable {
    case published(slug: String, version: String, topicUrl: String?)
    case failed(String)
}

@MainActor
final class CommunityAccount: ObservableObject {
    static let baseURL = URL(string: "https://store.byteplayer.app")!

    /// nil while signed out. Persisted (name only, not the token) in
    /// UserDefaults so the UI shows who's signed in without a round trip.
    @Published private(set) var user: CommunityUser?
    @Published private(set) var isLoggingIn = false
    @Published var loginError: String?

    private static let keychainService = "com.qiudaomao.DeskLayer.store"
    private static let userKey = "DeskLayer.communityUser"
    private var pollTask: Task<Void, Never>?
    private let log = Logger(subsystem: "com.qiudaomao.DeskLayer", category: "community")

    private let session: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 30
        config.urlCache = nil
        return URLSession(configuration: config)
    }()

    init() {
        if token != nil, let data = UserDefaults.standard.data(forKey: Self.userKey) {
            user = try? JSONDecoder().decode(CommunityUser.self, from: data)
        }
    }

    var token: String? {
        get { KeychainStore.secret(account: "token", service: Self.keychainService) }
        set { KeychainStore.setSecret(newValue, account: "token", service: Self.keychainService) }
    }

    var isSignedIn: Bool { token != nil && user != nil }

    // MARK: - Device-code login

    /// Opens the forum sign-in page in the browser and polls until the user
    /// completes it there (or ten minutes pass).
    func signIn() {
        guard !isLoggingIn else { return }
        isLoggingIn = true
        loginError = nil
        pollTask = Task { [weak self] in
            await self?.runDeviceFlow()
            self?.isLoggingIn = false
        }
    }

    func cancelSignIn() {
        pollTask?.cancel()
        pollTask = nil
        isLoggingIn = false
    }

    func signOut() {
        token = nil
        user = nil
        UserDefaults.standard.removeObject(forKey: Self.userKey)
    }

    private struct DeviceStart: Decodable {
        var deviceCode: String
        var loginUrl: String
        var expiresInSeconds: Int?
        var pollUrl: String
    }

    private struct TokenResponse: Decodable {
        var token: String?
        var status: String?
        var error: String?
        var user: CommunityUser?
    }

    private func runDeviceFlow() async {
        do {
            var start = URLRequest(url: Self.baseURL.appendingPathComponent("auth/device"))
            start.httpMethod = "POST"
            let (data, _) = try await session.data(for: start)
            let device = try JSONDecoder().decode(DeviceStart.self, from: data)
            guard let loginURL = URL(string: device.loginUrl),
                  let pollURL = URL(string: device.pollUrl) else {
                loginError = String(localized: "The store sent an unusable sign-in address.")
                return
            }
            NSWorkspace.shared.open(loginURL)

            // Poll until the browser side finishes. The deadline mirrors the
            // server's own device-code expiry.
            let deadline = Date().addingTimeInterval(TimeInterval(device.expiresInSeconds ?? 600))
            while Date() < deadline {
                try Task.checkCancellation()
                try await Task.sleep(for: .seconds(2))
                var poll = URLRequest(url: pollURL)
                poll.httpMethod = "POST"
                poll.setValue("application/json", forHTTPHeaderField: "Content-Type")
                poll.httpBody = try JSONEncoder().encode(["deviceCode": device.deviceCode])
                let (body, response) = try await session.data(for: poll)
                let status = (response as? HTTPURLResponse)?.statusCode ?? 0
                if status == 202 { continue }
                let decoded = try JSONDecoder().decode(TokenResponse.self, from: body)
                if status == 200, let token = decoded.token, let user = decoded.user {
                    self.token = token
                    self.user = user
                    if let data = try? JSONEncoder().encode(user) {
                        UserDefaults.standard.set(data, forKey: Self.userKey)
                    }
                    log.info("signed in as \(user.username, privacy: .public)")
                    return
                }
                loginError = decoded.error == "expired_or_unknown_device_code"
                    ? String(localized: "The sign-in expired. Try again.")
                    : (decoded.error ?? String(localized: "Sign-in failed."))
                return
            }
            loginError = String(localized: "The sign-in expired. Try again.")
        } catch is CancellationError {
            // The user closed the sheet; not an error.
        } catch {
            loginError = error.localizedDescription
        }
    }

    // MARK: - Publish

    private struct PublishResponse: Decodable {
        var slug: String?
        var version: String?
        var topicUrl: String?
        var error: String?
    }

    /// One-click publish. The backend validates the source, stores it
    /// immutably, and opens (or updates) the plugin's forum showcase topic.
    /// `previewPng` (≤2MB) becomes the listing's showcase screenshot.
    func publish(name: String, version: String, description: String,
                 source: String, permissions: [String],
                 previewPng: Data? = nil) async -> PublishResult {
        guard let token else { return .failed(String(localized: "Sign in first.")) }
        var request = URLRequest(url: Self.baseURL.appendingPathComponent("api/plugins"))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        var body: [String: String] = [
            "name": name,
            "version": version,
            "source": source,
        ]
        if !description.isEmpty { body["description"] = description }
        if !permissions.isEmpty { body["permissions"] = permissions.sorted().joined(separator: ", ") }
        if let previewPng { body["previewPng"] = previewPng.base64EncodedString() }
        do {
            request.httpBody = try JSONEncoder().encode(body)
            let (data, response) = try await session.data(for: request)
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            let decoded = try? JSONDecoder().decode(PublishResponse.self, from: data)
            switch status {
            case 200, 201:
                guard let slug = decoded?.slug, let version = decoded?.version else {
                    return .failed(String(localized: "The store returned an unexpected reply."))
                }
                log.info("published \(slug, privacy: .public) \(version, privacy: .public)")
                return .published(slug: slug, version: version, topicUrl: decoded?.topicUrl)
            case 401:
                // The token was revoked or outlived the account; a stale
                // "signed in" state would make every publish fail cryptically.
                signOut()
                return .failed(String(localized: "Your session expired — sign in again."))
            case 409:
                return .failed(decoded?.error
                    ?? String(localized: "That name is taken, or this version is already published."))
            case 429:
                return .failed(String(localized: "Daily publish limit reached — try again tomorrow."))
            default:
                return .failed(decoded?.error ?? "HTTP \(status)")
            }
        } catch {
            return .failed(error.localizedDescription)
        }
    }
}
