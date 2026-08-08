//
//  JSUIPrelude.swift
//  DeskLayer
//
//  Pure-JS view builders injected into every plugin context. Building a
//  tree costs zero JS→Swift bridge calls: builders make plain objects and
//  chainable modifier methods just append {name, args}. Methods are
//  invisible to JSON.stringify, so the tree serializes as clean data
//  matching ViewNode. Aliases: Section→VStack, Paragraph→Text.
//
//  Interactivity: Button/onTap/onTapGesture register a JS callback in a
//  per-render action table and serialize only a numeric action id. When the
//  native view fires, Swift calls __dl_invokeAction(id, x, y). The table is
//  rebuilt each render (reset in view()), so ids always match the tree just
//  produced. Input only reaches floating-window items — the wallpaper layer
//  ignores mouse events.
//

import Foundation

nonisolated enum JSUIPrelude {
    static let source = """
    (function (global) {
        var MODIFIERS = ['textColor', 'foregroundColor', 'fontSize', 'font', 'bold',
                         'padding', 'background', 'cornerRadius', 'frame', 'opacity',
                         'spacing', 'value', 'loop', 'muted', 'lineLimit'];

        global.__dl_actions = {};
        var nextActionId = 1;
        function registerAction(fn) {
            var id = nextActionId++;
            global.__dl_actions[id] = fn;
            return id;
        }

        function makeNode(type, text, children) {
            var node = {
                type: type,
                text: text === undefined ? null : text,
                modifiers: [],
                children: children || []
            };
            MODIFIERS.forEach(function (name) {
                Object.defineProperty(node, name, {
                    enumerable: false, // keep JSON.stringify output clean
                    value: function () {
                        node.modifiers.push({ name: name, args: Array.prototype.slice.call(arguments) });
                        return node;
                    }
                });
            });
            // Tap anywhere on this node; handler receives { x, y } in local points.
            Object.defineProperty(node, 'onTapGesture', {
                enumerable: false,
                value: function (handler) {
                    node.modifiers.push({ name: 'onTapGesture', args: [registerAction(handler)] });
                    return node;
                }
            });
            // Alias used on Button.
            Object.defineProperty(node, 'onTap', {
                enumerable: false,
                value: function (handler) {
                    node.modifiers.push({ name: 'onTap', args: [registerAction(handler)] });
                    return node;
                }
            });
            // TextField change; handler receives { text }.
            Object.defineProperty(node, 'onChange', {
                enumerable: false,
                value: function (handler) {
                    node.modifiers.push({ name: 'onChange', args: [registerAction(handler)] });
                    return node;
                }
            });
            return node;
        }

        function normalizeChildren(children) {
            if (children === undefined || children === null) { return []; }
            return Array.isArray(children) ? children : [children];
        }

        global.VStack = function (children) { return makeNode('VStack', null, normalizeChildren(children)); };
        global.HStack = function (children) { return makeNode('HStack', null, normalizeChildren(children)); };
        global.ZStack = function (children) { return makeNode('ZStack', null, normalizeChildren(children)); };
        global.Text = function (s) { return makeNode('Text', String(s), []); };
        global.Image = function (name) { return makeNode('Image', String(name), []); };
        global.Spacer = function () { return makeNode('Spacer', null, []); };
        // Button(label, handler?) — handler also settable via .onTap(fn).
        global.Button = function (label, handler) {
            var node = makeNode('Button', String(label), []);
            if (typeof handler === 'function') { node.onTap(handler); }
            return node;
        };
        // Plain rectangle: size it with .frame(w,h) and color it with
        // .background(css) — the building block for bars and dividers.
        global.Rect = function () { return makeNode('Rect', null, []); };
        global.Spinner = function () { return makeNode('Spinner', null, []); };
        // ProgressBar(value) — value 0…1.
        global.ProgressBar = function (value) { return makeNode('ProgressBar', String(value), []); };
        // TextField(placeholder).value(str).onChange(fn) — fn gets { text }.
        global.TextField = function (placeholder, onChange) {
            var node = makeNode('TextField', String(placeholder || ''), []);
            if (typeof onChange === 'function') {
                node.modifiers.push({ name: 'onChange', args: [registerAction(onChange)] });
            }
            return node;
        };
        // Video(url).loop(true).muted(false)
        global.Video = function (url) { return makeNode('Video', String(url), []); };

        // javascript-ui style aliases
        global.Section = global.VStack;
        global.Paragraph = global.Text;

        global.view = function (children) {
            return makeNode('Root', null, normalizeChildren(children));
        };

        // Reset the action table BEFORE a render builds its tree. It can't be
        // done inside view(): JS evaluates the child builders (which register
        // actions) before view() is called, so resetting there would wipe
        // them. Swift calls this just before invoking render().
        global.__dl_resetActions = function () {
            global.__dl_actions = {};
            nextActionId = 1;
        };

        // Called by Swift when a native Button/tap/text-change fires. The
        // payload is a JSON string: {} for a button, {x,y} for a tap,
        // {text} for a text field.
        global.__dl_invokeAction = function (id, payloadJSON) {
            var fn = global.__dl_actions[id];
            if (typeof fn !== 'function') { return; }
            var payload = {};
            try { payload = JSON.parse(payloadJSON); } catch (e) {}
            fn(payload);
        };
    })(this);
    """
}
