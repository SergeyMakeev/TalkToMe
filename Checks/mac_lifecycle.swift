import Cocoa
import SwiftUI

// Small diagnostic for AppKit Quit scheduling, without compiling the ML engines.
@MainActor final class State: ObservableObject { @Published var settings = false }
struct Content: View {
    @ObservedObject var state: State
    var body: some View {
        Text("Quit diagnostic").frame(width: 300, height: 200)
            .sheet(isPresented: $state.settings) { Text("Settings").padding(50) }
    }
}
@MainActor final class Delegate: NSObject, NSApplicationDelegate {
    let state = State()
    var window: NSWindow!
    var ready = false
    func applicationDidFinishLaunching(_ notification: Notification) {
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 300, height: 200),
            styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.contentView = NSHostingView(rootView: Content(state: state))
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        state.settings = CommandLine.arguments.contains("--sheet")
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) {
            print("Scheduling Quit; sheet=\(self.window.attachedSheet != nil)")
            fflush(stdout)
            let timer = Timer(timeInterval: 0.01, repeats: false) { _ in
                MainActor.assumeIsolated {
                    print("Timer fired; requesting Quit")
                    fflush(stdout)
                    NSApp.terminate(nil)
                    print("Terminate returned")
                    fflush(stdout)
                }
            }
            RunLoop.main.add(timer, forMode: .common)
        }
    }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        print("Delegate shouldTerminate called")
        fflush(stdout)
        if ready { return .terminateNow }
        DispatchQueue.global().async {
            RunLoop.main.perform(inModes: [.common, .modalPanel]) {
                MainActor.assumeIsolated {
                    print("Replying after cleanup")
                    fflush(stdout)
                    self.ready = true
                    sender.reply(toApplicationShouldTerminate: true)
                }
            }
            CFRunLoopWakeUp(CFRunLoopGetMain())
        }
        return .terminateLater
    }
    func applicationWillTerminate(_ notification: Notification) {
        print("Normal shutdown complete")
    }
}
MainActor.assumeIsolated {
    let app = NSApplication.shared
    let delegate = Delegate()
    app.setActivationPolicy(.regular)
    app.delegate = delegate
    withExtendedLifetime(delegate) { app.run() }
}
