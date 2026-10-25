import Foundation

/// JSON payloads of control messages (camelCase on the wire; Swift property names already are).
public enum ProtocolJSON {
    public static let encoder: JSONEncoder = JSONEncoder()
    public static let decoder: JSONDecoder = JSONDecoder()

    public static func decode<T: Decodable>(_ type: T.Type, from data: Data) throws -> T {
        try decoder.decode(type, from: data)
    }
}

public struct EmptyMessage: Codable, Equatable {
    public init() {}
}

public struct Hello: Codable, Equatable {
    public var proto: Int
    public var pcId: String
    public var pcName: String
    public var appVersion: String
}

public struct CameraInfo: Codable, Equatable {
    public var id: String
    public var torch: Bool

    public init(id: String, torch: Bool) {
        self.id = id
        self.torch = torch
    }
}

public struct FormatInfo: Codable, Equatable {
    public var w: Int
    public var h: Int
    public var fps: [Int]

    public init(w: Int, h: Int, fps: [Int]) {
        self.w = w
        self.h = h
        self.fps = fps
    }
}

public struct DeviceInfo: Codable, Equatable {
    public var deviceId: String
    public var deviceName: String
    public var model: String
    public var osVersion: String
    public var appVersion: String
    public var nonce: String
    public var paired: Bool
    public var cameras: [CameraInfo]
    public var formats: [FormatInfo]
    public var codecs: [String]

    public init(deviceId: String, deviceName: String, model: String, osVersion: String, appVersion: String,
                nonce: String, paired: Bool, cameras: [CameraInfo], formats: [FormatInfo], codecs: [String]) {
        self.deviceId = deviceId
        self.deviceName = deviceName
        self.model = model
        self.osVersion = osVersion
        self.appVersion = appVersion
        self.nonce = nonce
        self.paired = paired
        self.cameras = cameras
        self.formats = formats
        self.codecs = codecs
    }
}

public struct PairConfirm: Codable, Equatable {
    public var pcNonce: String
    public var proof: String
}

public struct Auth: Codable, Equatable {
    public var pcNonce: String
    public var mac: String
}

public struct AuthResult: Codable, Equatable {
    public var ok: Bool
    public var reason: String?

    public init(ok: Bool, reason: String? = nil) {
        self.ok = ok
        self.reason = reason
    }
}

public struct StartStream: Codable, Equatable {
    public var camera: String
    public var width: Int
    public var height: Int
    public var fps: Int
    public var codec: String
    public var quality: Double

    public init(camera: String, width: Int, height: Int, fps: Int, codec: String, quality: Double) {
        self.camera = camera
        self.width = width
        self.height = height
        self.fps = fps
        self.codec = codec
        self.quality = quality
    }
}

public struct StreamState: Codable, Equatable {
    public var state: String
    public var reason: String?
    public var camera: String?
    public var width: Int
    public var height: Int
    public var fps: Int
    public var codec: String?

    public init(state: String, reason: String? = nil, camera: String? = nil, width: Int = 0, height: Int = 0, fps: Int = 0, codec: String? = nil) {
        self.state = state
        self.reason = reason
        self.camera = camera
        self.width = width
        self.height = height
        self.fps = fps
        self.codec = codec
    }

    public static let streaming = "streaming"
    public static let stopped = "stopped"
    public static let paused = "paused"
}

public struct CameraSwitch: Codable, Equatable {
    public var camera: String
}

public struct SetResolution: Codable, Equatable {
    public var width: Int
    public var height: Int
}

public struct SetFps: Codable, Equatable {
    public var fps: Int
}

public struct SetQuality: Codable, Equatable {
    public var quality: Double
}

public struct SetTorch: Codable, Equatable {
    public var on: Bool
}

public struct Disconnect: Codable, Equatable {
    public var reason: String

    public init(reason: String) {
        self.reason = reason
    }
}

public struct ErrorMessage: Codable, Equatable {
    public var code: String
    public var message: String

    public init(code: String, message: String) {
        self.code = code
        self.message = message
    }

    public static let busy = "busy"
    public static let unauthorized = "unauthorized"
    public static let unsupported = "unsupported"
    public static let cameraUnavailable = "camera_unavailable"
    public static let badRequest = "bad_request"
}
