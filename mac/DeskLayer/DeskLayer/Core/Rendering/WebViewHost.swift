//
//  WebViewHost.swift
//  DeskLayer
//
//  Hosts a WKWebView for webview-mode plugins: loads a URL with an optional
//  custom user-agent, extra headers, and pre-seeded cookies, then scrolls to
//  (offsetX, offsetY) so the item shows a chosen region of the page. The item
//  frame clips the rest.
//

import AppKit
import WebKit
import os

@MainActor
final class WebViewHost: NSObject, WKNavigationDelegate {
    let webView: WKWebView
    private let config: WebViewConfig
    private let pluginID: String
    /// Throttled preview for the manager's virtual desktop (main thread).
    var onThumbnail: ((CGImage) -> Void)?
    private var thumbnailTimer: Timer?

    init(pluginID: String, config: WebViewConfig, frame: CGRect) {
        self.pluginID = pluginID
        self.config = config

        let configuration = WKWebViewConfiguration()
        configuration.suppressesIncrementalRendering = false
        webView = WKWebView(frame: CGRect(origin: .zero, size: frame.size), configuration: configuration)
        super.init()

        webView.navigationDelegate = self
        webView.setValue(false, forKey: "drawsBackground") // transparent so item background shows
        webView.autoresizingMask = [.width, .height]
        if let ua = config.userAgent, !ua.isEmpty {
            webView.customUserAgent = ua
        }
        if config.zoom != 1, config.zoom > 0 {
            webView.pageZoom = config.zoom
        }
    }

    func start() {
        guard let url = URL(string: config.url), url.scheme != nil else {
            renderLog.error("[\(self.pluginID, privacy: .public)] webview: invalid url \(self.config.url, privacy: .public)")
            return
        }
        // Seed cookies first, then load (both are async in order on the store).
        let store = webView.configuration.websiteDataStore.httpCookieStore
        let group = DispatchGroup()
        for fields in config.cookies {
            guard let cookie = Self.makeCookie(fields, defaultHost: url.host) else { continue }
            group.enter()
            store.setCookie(cookie) { group.leave() }
        }
        group.notify(queue: .main) { [weak self] in
            guard let self else { return }
            var request = URLRequest(url: url)
            for (key, value) in self.config.headers {
                request.setValue(value, forHTTPHeaderField: key)
            }
            self.webView.load(request)
        }
    }

    func stop() {
        thumbnailTimer?.invalidate()
        thumbnailTimer = nil
        webView.stopLoading()
        webView.navigationDelegate = nil
        webView.removeFromSuperview()
    }

    func nsView() -> NSView { webView }

    // MARK: - WKNavigationDelegate

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        // Show the requested region of the page.
        if config.offsetX != 0 || config.offsetY != 0 {
            webView.evaluateJavaScript("window.scrollTo(\(config.offsetX), \(config.offsetY));", completionHandler: nil)
        }
        startThumbnails()
    }

    // MARK: - Thumbnails (for the manager's virtual desktop)

    private func startThumbnails() {
        guard onThumbnail != nil, thumbnailTimer == nil else { return }
        captureThumbnail()
        thumbnailTimer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.captureThumbnail() }
        }
    }

    private func captureThumbnail() {
        guard let onThumbnail else { return }
        let snapConfig = WKSnapshotConfiguration()
        snapConfig.afterScreenUpdates = false
        webView.takeSnapshot(with: snapConfig) { image, _ in
            guard let image,
                  let cg = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return }
            onThumbnail(cg)
        }
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        renderLog.error("[\(self.pluginID, privacy: .public)] webview load failed: \(error.localizedDescription, privacy: .public)")
    }

    // MARK: - Cookies

    private static func makeCookie(_ fields: [String: String], defaultHost: String?) -> HTTPCookie? {
        guard let name = fields["name"], let value = fields["value"] else { return nil }
        var properties: [HTTPCookiePropertyKey: Any] = [
            .name: name,
            .value: value,
            .path: fields["path"] ?? "/",
        ]
        if let domain = fields["domain"] ?? defaultHost {
            properties[.domain] = domain
        }
        return HTTPCookie(properties: properties)
    }
}
