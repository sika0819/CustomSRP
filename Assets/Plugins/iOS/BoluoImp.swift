import Foundation

/// iOS IMP 邮箱（编进 UnityFramework，C# `DllImport __Internal`）。
/// 宿主 SwiftUI 在加载 Unity 后 `BoluoImp_RegisterHost/Sign` 挂上 Notify/Query/Sign。
/// Android 仍走 `libboluoimp.so`（`boluo_bridge_push.c`），不要在这里加 JNI。
private enum BoluoImpSlots {
    static let lock = NSLock()
    static var push: (@convention(c) (Int32, Int32, Int32, Int32) -> Void)?
    static var notify: (@convention(c) (Int32, Int32, Int32, Int32) -> Void)?
    static var query: (
        @convention(c) (Int32, Int32, Int32, Int32, UnsafePointer<CChar>?) -> UnsafePointer<CChar>?
    )?
    static var sign: (@convention(c) (UnsafePointer<CChar>?) -> UnsafePointer<CChar>?)?
}

private let kQueryNotReady: UnsafePointer<CChar> = {
    let bytes = Array("-2|".utf8CString)
    let p = UnsafeMutablePointer<CChar>.allocate(capacity: bytes.count)
    for i in 0..<bytes.count { p[i] = bytes[i] }
    return UnsafePointer(p)
}()

@_used
@_cdecl("BoluoImp_RegisterPush")
func BoluoImp_RegisterPush(
    _ fn: (@convention(c) (Int32, Int32, Int32, Int32) -> Void)?
) {
    BoluoImpSlots.lock.lock()
    BoluoImpSlots.push = fn
    BoluoImpSlots.lock.unlock()
}

@_used
@_cdecl("BoluoImp_UnregisterPush")
func BoluoImp_UnregisterPush() {
    BoluoImpSlots.lock.lock()
    BoluoImpSlots.push = nil
    BoluoImpSlots.lock.unlock()
}

@_used
@_cdecl("BoluoImp_Push")
func BoluoImp_Push(_ type: Int32, _ a: Int32, _ b: Int32, _ c: Int32) {
    BoluoImpSlots.lock.lock()
    let fn = BoluoImpSlots.push
    BoluoImpSlots.lock.unlock()
    fn?(type, a, b, c)
}

@_used
@_cdecl("BoluoImp_RegisterHost")
func BoluoImp_RegisterHost(
    _ notifyFn: (@convention(c) (Int32, Int32, Int32, Int32) -> Void)?,
    _ queryFn: (
        @convention(c) (Int32, Int32, Int32, Int32, UnsafePointer<CChar>?) -> UnsafePointer<CChar>?
    )?
) {
    BoluoImpSlots.lock.lock()
    BoluoImpSlots.notify = notifyFn
    BoluoImpSlots.query = queryFn
    BoluoImpSlots.lock.unlock()
}

@_used
@_cdecl("BoluoImp_RegisterSign")
func BoluoImp_RegisterSign(
    _ signFn: (@convention(c) (UnsafePointer<CChar>?) -> UnsafePointer<CChar>?)?
) {
    BoluoImpSlots.lock.lock()
    BoluoImpSlots.sign = signFn
    BoluoImpSlots.lock.unlock()
}

@_used
@_cdecl("BoluoImp_Notify")
func BoluoImp_Notify(_ type: Int32, _ a: Int32, _ b: Int32, _ c: Int32) {
    BoluoImpSlots.lock.lock()
    let fn = BoluoImpSlots.notify
    BoluoImpSlots.lock.unlock()
    fn?(type, a, b, c)
}

@_used
@_cdecl("BoluoImp_Query")
func BoluoImp_Query(
    _ type: Int32,
    _ a: Int32,
    _ b: Int32,
    _ c: Int32,
    _ inBlob: UnsafePointer<CChar>?
) -> UnsafePointer<CChar>? {
    BoluoImpSlots.lock.lock()
    let fn = BoluoImpSlots.query
    BoluoImpSlots.lock.unlock()
    guard let fn else { return kQueryNotReady }
    return fn(type, a, b, c, inBlob)
}

@_used
@_cdecl("BoluoImp_GenerateSign")
func BoluoImp_GenerateSign(_ jsonBody: UnsafePointer<CChar>?) -> UnsafePointer<CChar>? {
    BoluoImpSlots.lock.lock()
    let fn = BoluoImpSlots.sign
    BoluoImpSlots.lock.unlock()
    return fn?(jsonBody)
}

@_used
private let _boluoImpKeep: Void = {
    _ = BoluoImp_RegisterPush as Any
    _ = BoluoImp_UnregisterPush as Any
    _ = BoluoImp_Push as Any
    _ = BoluoImp_RegisterHost as Any
    _ = BoluoImp_RegisterSign as Any
    _ = BoluoImp_Notify as Any
    _ = BoluoImp_Query as Any
    _ = BoluoImp_GenerateSign as Any
}()

