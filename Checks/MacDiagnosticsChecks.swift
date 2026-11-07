import Foundation

@MainActor func checkDiagnostics() throws {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent("TalkToMe-diagnostics-" + UUID().uuidString)
    let crashes = directory.appendingPathComponent("system")
    defer { try? FileManager.default.removeItem(at: directory) }
    try FileManager.default.createDirectory(at: crashes, withIntermediateDirectories: true)
    let fixture = #"{"bug_type":"309","name":"TalkToMe"}"# + "\n" + #"""
    {"procName":"TalkToMe","pid":123,"captureTime":"2026-11-07","faultingThread":0,
      "procPath":"/Users/PRIVATE/Applications/TalkToMe.app/Contents/MacOS/TalkToMe",
      "exception":{"type":"EXC_BAD_ACCESS","signal":"SIGSEGV","codes":"0x1","message":"PRIVATE"},
      "termination":{"namespace":"SIGNAL","code":11,"indicator":"PRIVATE"},
      "asi":{"application_specific_information":["PRIVATE"]},
      "threads":[{"threadState":{"register":"PRIVATE"},"frames":[{"imageIndex":0,"imageOffset":42,"symbol":"testCrash"}]}],
      "usedImages":[{"name":"TalkToMe","path":"/Users/PRIVATE/TalkToMe","uuid":"A-B-C"}]}
    """#
    guard let summary = CrashSummary.render(fixture, format: "ips"), summary.contains("EXC_BAD_ACCESS"),
          summary.contains("testCrash"), summary.contains("+0x2a"), !summary.contains("PRIVATE") else { throw Failure("Crash summary lost stack data or included private content.") }
    guard CrashSummary.render(fixture.replacingOccurrences(of: #""procName":"TalkToMe""#, with: #""procName":"AnotherApp""#), format: "ips") == nil,
          CrashSummary.render("bad data", format: "ips") == nil else { throw Failure("Unrelated or invalid crash accepted.") }
    let legacy = """
    Process: TalkToMe [123]
    Version: 0.4.1
    Exception Type: EXC_BAD_ACCESS (SIGSEGV)
    Application Specific Information:
    PRIVATE
    Thread 0 Crashed:
    0 TalkToMe 0x12345 testCrash + 42

    Binary Images:
    PRIVATE
    """
    guard let old = CrashSummary.render(legacy, format: "crash"), old.contains("testCrash"), !old.contains("PRIVATE") else { throw Failure("Legacy crash summary failed.") }
    try fixture.write(to: crashes.appendingPathComponent("TalkToMe-test.ips"), atomically: true, encoding: .utf8)
    Diagnostics.initialize(at: directory, crashReports: crashes)
    Diagnostics.event("test-stage", "first session")
    Diagnostics.error("test-error", Failure("PRIVATE"))
    var prefs = Preferences()
    prefs.prompt = "PRIVATE"; prefs.glossary = ["PRIVATE"]; prefs.serverURL = "https://PRIVATE"; prefs.microphone = "PRIVATE"
    let report = Diagnostics.report(prefs)
    guard report.contains("first session"), report.contains("testCrash"), report.contains("test-error"), !report.contains("PRIVATE") else { throw Failure("Debug report missing evidence or leaking private data.") }
    Diagnostics.cleanExit()
    guard !FileManager.default.fileExists(atPath: directory.appendingPathComponent("session.json").path) else { throw Failure("Clean exit marker not removed.") }
    Diagnostics.initialize(at: directory, crashReports: crashes)
    guard Diagnostics.report(prefs).contains("first session") else { throw Failure("Restart erased previous diagnostics.") }
    try String(repeating: "x", count: 2_000_010).write(to: directory.appendingPathComponent("events.log"), atomically: true, encoding: .utf8)
    Diagnostics.event("rotation-check")
    guard FileManager.default.fileExists(atPath: directory.appendingPathComponent("events.log.previous").path),
          (try String(contentsOf: directory.appendingPathComponent("events.log"))).contains("rotation-check") else { throw Failure("Log rotation failed.") }
    Diagnostics.cleanExit()
}
