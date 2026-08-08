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

import Foundation

nonisolated enum JSUIPrelude {
    static let source = """
    (function (global) {
        var MODIFIERS = ['textColor', 'foregroundColor', 'fontSize', 'font', 'bold',
                         'padding', 'background', 'cornerRadius', 'frame', 'opacity',
                         'spacing'];

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

        // javascript-ui style aliases
        global.Section = global.VStack;
        global.Paragraph = global.Text;

        global.view = function (children) { return makeNode('Root', null, normalizeChildren(children)); };
    })(this);
    """
}
