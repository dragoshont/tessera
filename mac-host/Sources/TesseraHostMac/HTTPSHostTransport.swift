import Foundation
import TesseraHostCore

public struct HostServerDescriptor: Equatable, Sendable {
    public let baseURL: URL

    public init(baseURL: URL) throws {
        guard baseURL.scheme == "https",
              baseURL.user == nil,
              baseURL.password == nil,
              baseURL.query == nil,
              baseURL.fragment == nil,
              baseURL.path == "" || baseURL.path == "/",
              baseURL.port == nil,
              let host = baseURL.host,
              !host.isEmpty,
              host.lowercased() != "localhost",
              !host.lowercased().hasSuffix(".local"),
              !Self.isIPv4(host),
              host.utf8.count <= 253,
              host.utf8.allSatisfy({
                ($0 >= 65 && $0 <= 90) || ($0 >= 97 && $0 <= 122) ||
                ($0 >= 48 && $0 <= 57) || $0 == 45 || $0 == 46
              })
        else { throw HostChannelError.invalidServerDescriptor }
        self.baseURL = baseURL
    }

    private static func isIPv4(_ value: String) -> Bool {
        let components = value.split(separator: ".", omittingEmptySubsequences: false)
        return components.count == 4 && components.allSatisfy {
            !$0.isEmpty && $0.count <= 3 && $0.allSatisfy(\.isNumber) && Int($0).map { (0...255).contains($0) } == true
        }
    }
}

public final class URLSessionHostHTTPExecutor: NSObject, HostHTTPExecutor, URLSessionTaskDelegate, @unchecked Sendable {
    private let descriptor: HostServerDescriptor
    private let maximumResponseBytes: Int
    private let session: URLSession

    public init(
        descriptor: HostServerDescriptor,
        configuration: URLSessionConfiguration = .ephemeral,
        maximumResponseBytes: Int = 2 * 1024 * 1024
    ) {
        self.descriptor = descriptor
        self.maximumResponseBytes = maximumResponseBytes
        configuration.timeoutIntervalForRequest = 35
        configuration.timeoutIntervalForResource = 40
        configuration.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        configuration.urlCache = nil
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.waitsForConnectivity = false
        self.session = URLSession(configuration: configuration, delegate: nil, delegateQueue: nil)
        super.init()
    }

    public func execute(path: String, request: PreparedHostRequest) async throws -> HostHTTPResponse {
        guard path.hasPrefix("/host-channel/"),
              !path.contains(".."),
              !path.contains("?"),
              !path.contains("#"),
              let url = URL(string: path, relativeTo: descriptor.baseURL)?.absoluteURL,
              url.host == descriptor.baseURL.host,
              url.scheme == "https"
        else { throw HostChannelError.invalidPath }
        var urlRequest = URLRequest(url: url)
        urlRequest.httpMethod = "POST"
        urlRequest.httpBody = request.body
        urlRequest.cachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        urlRequest.setValue("application/json", forHTTPHeaderField: "Content-Type")
        urlRequest.setValue("identity", forHTTPHeaderField: "Accept-Encoding")
        for (name, value) in request.headers { urlRequest.setValue(value, forHTTPHeaderField: name) }
        let (data, response) = try await BoundedHTTP.read(
            session: session,
            request: urlRequest,
            delegate: self,
            maximumBytes: maximumResponseBytes
        )
        guard let http = response as? HTTPURLResponse,
              http.url?.host == descriptor.baseURL.host,
              http.url?.scheme == "https",
              BoundedHTTP.hasIdentityEncoding(http)
        else { throw HostChannelError.invalidResponse }
        let contentType = BoundedHTTP.baseContentType(http)
        let validJSON = (try? JSONSerialization.jsonObject(with: data)) is [String: Any]
        let consumed = contentType == "application/json" && http.statusCode < 500 && validJSON
        let unconsumed = contentType == "application/problem+json" || http.statusCode >= 500
        guard consumed || unconsumed else { throw HostChannelError.invalidResponse }
        return HostHTTPResponse(
            statusCode: http.statusCode,
            body: data,
            envelopeConsumed: consumed
        )
    }

    public func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        willPerformHTTPRedirection response: HTTPURLResponse,
        newRequest request: URLRequest,
        completionHandler: @escaping @Sendable (URLRequest?) -> Void
    ) {
        completionHandler(nil)
    }
}

enum BoundedHTTP {
    static func read(
        session: URLSession,
        request: URLRequest,
        delegate: URLSessionTaskDelegate,
        maximumBytes: Int
    ) async throws -> (Data, URLResponse) {
        let (bytes, response) = try await session.bytes(for: request, delegate: delegate)
        if let expected = response.expectedContentLength as Int64?, expected > Int64(maximumBytes) {
            throw HostChannelError.responseTooLarge
        }
        var data = Data()
        data.reserveCapacity(min(maximumBytes, max(0, Int(response.expectedContentLength))))
        for try await byte in bytes {
            guard data.count < maximumBytes else { throw HostChannelError.responseTooLarge }
            data.append(byte)
        }
        return (data, response)
    }

    static func baseContentType(_ response: HTTPURLResponse) -> String {
        response.value(forHTTPHeaderField: "Content-Type")?.split(separator: ";", maxSplits: 1).first?
            .trimmingCharacters(in: .whitespacesAndNewlines).lowercased() ?? ""
    }

    static func hasIdentityEncoding(_ response: HTTPURLResponse) -> Bool {
        guard let value = response.value(forHTTPHeaderField: "Content-Encoding") else { return true }
        return value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() == "identity"
    }
}