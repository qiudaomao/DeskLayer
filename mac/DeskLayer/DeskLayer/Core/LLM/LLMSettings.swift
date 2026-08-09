//
//  LLMSettings.swift
//  DeskLayer
//
//  Where the "Create Plugin" feature sends its requests. Any OpenAI-compatible
//  endpoint works — the base URL is the only thing that changes between
//  OpenAI, DeepSeek, Moonshot, OpenRouter, Ollama and LM Studio.
//
//  The API key is deliberately absent from the Codable keys: it lives in the
//  login keychain, the same rule SSH passwords follow.
//

import Foundation

nonisolated struct LLMSettings: Codable, Equatable {
    /// Everything before `/chat/completions`. Trailing slashes are tolerated.
    var baseURL: String = "https://api.openai.com/v1"
    var model: String = "gpt-4o"
    /// How many times the model may call tools before the run is stopped.
    /// A confused model can otherwise read files forever.
    var maxTurns: Int = 12
    /// Models last fetched from the endpoint. Kept so the picker is populated
    /// on the next launch without asking the server again — the list only
    /// changes when the user fetches it.
    var cachedModels: [String] = []

    private enum CodingKeys: String, CodingKey { case baseURL, model, maxTurns, cachedModels }

    init(baseURL: String = "https://api.openai.com/v1",
         model: String = "gpt-4o",
         maxTurns: Int = 12,
         cachedModels: [String] = []) {
        self.baseURL = baseURL
        self.model = model
        self.maxTurns = maxTurns
        self.cachedModels = cachedModels
    }

    /// Every field optional on the way in, so settings written by an older
    /// build still load after a field is added.
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        baseURL = try c.decodeIfPresent(String.self, forKey: .baseURL) ?? "https://api.openai.com/v1"
        model = try c.decodeIfPresent(String.self, forKey: .model) ?? "gpt-4o"
        maxTurns = try c.decodeIfPresent(Int.self, forKey: .maxTurns) ?? 12
        cachedModels = try c.decodeIfPresent([String].self, forKey: .cachedModels) ?? []
    }

    /// `{baseURL}/chat/completions`, however the user typed the base.
    var completionsURL: URL? {
        let trimmed = baseURL.trimmingCharacters(in: .whitespacesAndNewlines)
            .trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        guard !trimmed.isEmpty else { return nil }
        // A base URL that already names the endpoint is used as given, so
        // pasting the full URL from a provider's docs works too.
        if trimmed.hasSuffix("/chat/completions") { return URL(string: trimmed) }
        return URL(string: trimmed + "/chat/completions")
    }

    /// `{baseURL}/models` — the OpenAI-compatible listing endpoint.
    var modelsURL: URL? {
        var trimmed = baseURL.trimmingCharacters(in: .whitespacesAndNewlines)
            .trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        if trimmed.hasSuffix("/chat/completions") {
            trimmed = String(trimmed.dropLast("/chat/completions".count))
        }
        guard !trimmed.isEmpty else { return nil }
        return URL(string: trimmed + "/models")
    }

    var isConfigured: Bool {
        completionsURL != nil && !model.trimmingCharacters(in: .whitespaces).isEmpty
    }

    // MARK: - Persistence

    private static let defaultsKey = "DeskLayer.llm"
    private static let keyAccount = "apiKey"

    static func load() -> LLMSettings {
        guard let data = UserDefaults.standard.data(forKey: defaultsKey),
              let decoded = try? JSONDecoder().decode(LLMSettings.self, from: data)
        else { return LLMSettings() }
        return decoded
    }

    func save() {
        guard let data = try? JSONEncoder().encode(self) else { return }
        UserDefaults.standard.set(data, forKey: Self.defaultsKey)
    }

    /// Keychain, never UserDefaults — a key in a plist is a key on disk.
    static var apiKey: String? {
        get { KeychainStore.secret(account: keyAccount, service: KeychainStore.llmService) }
        set { KeychainStore.setSecret(newValue, account: keyAccount, service: KeychainStore.llmService) }
    }
}
