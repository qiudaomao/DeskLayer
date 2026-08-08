//
//  ItemRenderer.swift
//  DeskLayer
//
//  The pixel side of one running item: IOSurface-backed CGContexts driving
//  the plugin's render(ctx). renderFrame() must only be called on the
//  instance's queue; it returns the surface ready for layer.contents.
//
//  IOSurface (not CGBitmapContext.makeImage) because assigning a plain
//  CGImage to layer.contents makes Core Animation re-render it on the main
//  thread every frame (CA::Render::create_image_by_rendering — measured in
//  the M0 spike at ~65% of a core). An IOSurface is shared with WindowServer
//  zero-copy. Triple-buffered: WindowServer may still be scanning out the
//  previously committed surface. Canvas2D content persists across frames,
//  so each frame starts by carrying the previous frame's pixels forward.
//

import CoreGraphics
import CoreVideo
import Foundation
import ImageIO
import IOSurface
import JavaScriptCore
import os

nonisolated let renderSignposter = OSSignposter(subsystem: "com.qiudaomao.DeskLayer", category: "render")
nonisolated let renderLog = Logger(subsystem: "com.qiudaomao.DeskLayer", category: "runtime")

nonisolated final class ItemRenderer: @unchecked Sendable {
    let instance: PluginInstance
    var queue: DispatchQueue { instance.queue }
    var fps: Double { instance.fps }
    var isErrored: Bool { instance.isErrored }

    private struct Buffer {
        let surface: IOSurface
        let cg: CGContext
    }

    private let buffers: [Buffer]
    private let canvas: CanvasContext
    private let ctxValue: JSValue
    private var frameIndex = 0

    /// - Parameters:
    ///   - size: item size in points; buffers are size × scale pixels.
    ///   - assetsURL: the .deskplugin folder for ctx.drawImage lookups (nil
    ///     for bare .js plugins).
    init?(instance: PluginInstance, size: CGSize, scale: CGFloat, assetsURL: URL? = nil) {
        self.instance = instance

        let pixelWidth = Int(size.width * scale)
        let pixelHeight = Int(size.height * scale)
        guard pixelWidth > 0, pixelHeight > 0 else { return nil }

        var madeBuffers: [Buffer] = []
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        let bitmapInfo = CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue
        for _ in 0..<3 {
            guard let surface = IOSurface(properties: [
                .width: pixelWidth,
                .height: pixelHeight,
                .bytesPerElement: 4,
                .pixelFormat: kCVPixelFormatType_32BGRA,
            ]) else { return nil }
            guard let cg = CGContext(
                data: surface.baseAddress,
                width: pixelWidth,
                height: pixelHeight,
                bitsPerComponent: 8,
                bytesPerRow: surface.bytesPerRow,
                space: space,
                bitmapInfo: bitmapInfo
            ) else { return nil }
            // Flip to a top-left origin in point units, Canvas2D-style.
            cg.translateBy(x: 0, y: CGFloat(pixelHeight))
            cg.scaleBy(x: scale, y: -scale)
            cg.setAllowsAntialiasing(true)
            cg.interpolationQuality = .default
            madeBuffers.append(Buffer(surface: surface, cg: cg))
        }
        self.buffers = madeBuffers

        canvas = CanvasContext(cg: buffers[0].cg, widthPts: size.width, heightPts: size.height)
        canvas.propertyProvider = { [weak instance] name in
            instance?.property(named: name)?.jsValue
        }
        if let assetsURL {
            let cache = ImageCache(directory: assetsURL)
            canvas.imageProvider = { name in cache.image(named: name) }
        }
        ctxValue = JSValue(object: canvas, in: instance.context)
    }

    /// Loads and caches plugin-folder images for drawImage. Lookup is by
    /// file name within the plugin folder only — no path traversal.
    private final class ImageCache: @unchecked Sendable {
        private let directory: URL
        private var cache: [String: CGImage?] = [:]

        init(directory: URL) {
            self.directory = directory
        }

        func image(named name: String) -> CGImage? {
            if let hit = cache[name] { return hit }
            let fileName = (name as NSString).lastPathComponent
            let url = directory.appendingPathComponent(fileName)
            var loaded: CGImage?
            if let source = CGImageSourceCreateWithURL(url as CFURL, nil) {
                loaded = CGImageSourceCreateImageAtIndex(source, 0, nil)
            }
            cache[name] = loaded
            return loaded
        }
    }

    /// Runs one frame of plugin JS. Call only on `queue`.
    /// The returned IOSurface is ready to assign to layer.contents.
    func renderFrame() -> IOSurface? {
        guard !instance.isErrored else { return nil }
        let buffer = buffers[frameIndex % buffers.count]

        buffer.surface.lock(options: [], seed: nil)
        // Canvas2D semantics: content persists across frames. Carry the
        // previous frame forward into this (older) back buffer.
        if frameIndex == 0 {
            buffer.cg.clear(CGRect(x: 0, y: 0, width: canvas.width, height: canvas.height))
        } else {
            let previous = buffers[(frameIndex - 1) % buffers.count]
            memcpy(buffer.surface.baseAddress, previous.surface.baseAddress,
                   buffer.surface.bytesPerRow * buffer.surface.height)
        }
        frameIndex += 1
        canvas.beginFrame(on: buffer.cg)

        let jsInterval = renderSignposter.beginInterval("js", id: renderSignposter.makeSignpostID())
        let ok = instance.callRender(with: ctxValue)
        renderSignposter.endInterval("js", jsInterval)

        buffer.cg.flush()
        buffer.surface.unlock(options: [], seed: nil)
        return ok ? buffer.surface : nil
    }

    /// A CGImage copy of the most recently rendered frame, for the manager's
    /// virtual-desktop thumbnails. Call only on `queue`, after renderFrame().
    func makeThumbnailImage() -> CGImage? {
        guard frameIndex > 0 else { return nil }
        return buffers[(frameIndex - 1) % buffers.count].cg.makeImage()
    }

    /// Debug: renders one frame and writes it as PNG (async on `queue`).
    /// Also emits the PNG as base64 on stderr — the sandbox container is
    /// unreadable from outside, but a terminal launch can collect stderr.
    func writeDebugSnapshot(to url: URL) {
        queue.async {
            guard self.renderFrame() != nil else {
                FileHandle.standardError.write(Data("[\(self.instance.pluginID)] snapshot: renderFrame nil\n".utf8))
                return
            }
            let lastBuffer = self.buffers[(self.frameIndex - 1) % self.buffers.count]
            guard let image = lastBuffer.cg.makeImage(),
                  let destination = CGImageDestinationCreateWithURL(url as CFURL, "public.png" as CFString, 1, nil)
            else { return }
            CGImageDestinationAddImage(destination, image, nil)
            CGImageDestinationFinalize(destination)
            if let png = try? Data(contentsOf: url) {
                FileHandle.standardError.write(Data("SNAPSHOT:\(self.instance.pluginID):\(png.base64EncodedString())\n".utf8))
            }
        }
    }
}
