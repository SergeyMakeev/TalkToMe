import AppKit

guard CommandLine.arguments.count == 2, let pid = Int32(CommandLine.arguments[1]) else {
    fputs("Expected the running TalkToMe process.\n", stderr)
    exit(1)
}
let deadline = Date().addingTimeInterval(20)
var target: NSRunningApplication?
while Date() < deadline {
    if let app = NSRunningApplication(processIdentifier: pid), app.bundleIdentifier == "com.sergeymakeev.TalkToMe",
       app.isFinishedLaunching, !app.isTerminated { target = app; break }
    RunLoop.current.run(until: Date().addingTimeInterval(0.1))
}
// Let first-run Settings finish presentation before issuing the user-level Quit.
RunLoop.current.run(until: Date().addingTimeInterval(1))
guard let app = target, !app.isTerminated, app.terminate() else {
    fputs("TalkToMe did not launch or accept the normal Quit request.\n", stderr)
    exit(1)
}
print("Requested normal application termination.")
