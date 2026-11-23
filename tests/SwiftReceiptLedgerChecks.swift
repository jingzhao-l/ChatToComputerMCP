import Foundation
import Darwin

private func require(_ condition: @autoclosure () -> Bool, _ label: String) {
    precondition(condition(), "Receipt regression: \(label)")
}

func runReceiptLedgerChecks() throws {
    let root = FileManager.default.temporaryDirectory
        .appendingPathComponent("ctcmcp-ledger-\(UUID().uuidString)")
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: root) }
    let location = root.appendingPathComponent("private/receipts.json")

    let first = try CommandReceiptLedger(fileURL: location)
    require(first.summary().isEmpty, "initial empty")
    try first.reserve(scopeID: "default", requestID: "alpha")
    require(first.summary()["starting"] == 1, "pre-launch tombstone")
    let text = try String(contentsOf: location, encoding: .utf8)
    require(!text.contains("alpha") && !text.contains("default"), "no plaintext IDs")
    require(!text.contains("/Volumes/"), "no absolute paths")
    let filePermissions = try FileManager.default.attributesOfItem(atPath: location.path)
    let directoryPermissions = try FileManager.default.attributesOfItem(atPath: location.deletingLastPathComponent().path)
    require((filePermissions[.posixPermissions] as? NSNumber)?.intValue == 0o600,
            "file permissions 0600")
    require((directoryPermissions[.posixPermissions] as? NSNumber)?.intValue == 0o700,
            "folder permissions 0700")

    // Simulate unexpected app termination before a command callback completes.
    let resumed = try CommandReceiptLedger(fileURL: location)
    require(resumed.summary()["interrupted"] == 1, "incomplete operation is marked interrupted")
    do {
        try resumed.reserve(scopeID: "default", requestID: "alpha")
        fatalError("old request unexpectedly replayed")
    } catch CommandReceiptLedger.LedgerError.duplicateRequest { }
    require(resumed.summary()["interrupted"] == 1, "no automatic rerun")

    try resumed.reserve(scopeID: "other", requestID: "alpha")
    try resumed.finish(scopeID: "other", requestID: "alpha",
                       state: "exited", exitCode: 0)
    require(resumed.summary()["exited"] == 1, "different scope and terminal code")
    try resumed.reserve(scopeID: "default", requestID: "cancel")
    try resumed.finish(scopeID: "default", requestID: "cancel",
                       state: "cancelled", exitCode: 143)
    require(resumed.summary()["cancelled"] == 1, "cancel terminal receipt")

    try resumed.reserve(scopeID: "owner-scope", requestID: "owned-request", ownerID: "verified-client-one")
    let owned = try resumed.ownReceipt(scopeID: "owner-scope", requestID: "owned-request",
                                       ownerID: "verified-client-one")
    require(owned["state"] as? String == "starting" && owned["reattachable"] as? Bool == false,
            "the authenticated owner can inspect its own durable receipt")
    for untrusted in ["verified-client-two", ""] {
        do {
            _ = try resumed.ownReceipt(scopeID: "owner-scope", requestID: "owned-request", ownerID: untrusted)
            fatalError("a different owner read a durable receipt")
        } catch CommandReceiptLedger.LedgerError.receiptNotOwned { }
    }
    do {
        _ = try resumed.ownReceipt(scopeID: "default", requestID: "alpha", ownerID: "verified-client-one")
        fatalError("legacy unowned receipt was exposed")
    } catch CommandReceiptLedger.LedgerError.receiptNotOwned { }
    let privateText = try String(contentsOf: location, encoding: .utf8)
    require(!privateText.contains("owned-request") && !privateText.contains("verified-client-one"),
            "persistent receipt stores salted owner hash, never plaintext owner or request IDs")

    let third = try CommandReceiptLedger(fileURL: location)
    require(third.summary()["interrupted"] == 1, "terminal records preserved across restart")
    require(third.summary()["exited"] == 1, "completed receipt persisted")
    require(third.summary()["cancelled"] == 1, "cancel receipt persisted")
    let recovered = try third.ownReceipt(scopeID: "owner-scope", requestID: "owned-request",
                                         ownerID: "verified-client-one")
    require(recovered["state"] as? String == "interrupted" && recovered["reattachable"] as? Bool == false,
            "restart preserves owner while marking an unfinished command interrupted")
    do {
        try third.finish(scopeID: "default", requestID: "unknown",
                         state: "exited", exitCode: 0)
        fatalError("missing receipt accepted")
    } catch CommandReceiptLedger.LedgerError.missingReceipt { }

    // Reject corruption instead of silently resetting the idempotency ledger.
    try Data("{broken".utf8).write(to: location, options: .atomic)
    do {
        _ = try CommandReceiptLedger(fileURL: location)
        fatalError("corrupt receipt history was silently reset")
    } catch CommandReceiptLedger.LedgerError.invalidStore { }

    // File-level symlinks must not redirect audit records outside the app's
    // dedicated private directory.
    try FileManager.default.removeItem(at: location)
    let outside = root.appendingPathComponent("outside.json")
    try Data("unmodified".utf8).write(to: outside)
    try FileManager.default.createSymbolicLink(at: location,
                                                withDestinationURL: outside)
    do {
        _ = try CommandReceiptLedger(fileURL: location)
        fatalError("receipt symlink was accepted")
    } catch CommandReceiptLedger.LedgerError.unsafePath { }
    let outsideContent = try String(contentsOf: outside, encoding: .utf8)
    require(outsideContent == "unmodified", "outside file remains untouched")
    print("PASS: macOS durable receipt journal, no plaintext IDs, 0600/0700, no replay after restart, corruption and symlink fail closed")
}
