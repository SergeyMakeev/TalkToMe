import Cocoa

// Compile the actual delivery policy and pasteboard code before the ML engines.
// This executable does not post keyboard events or access the general clipboard.
struct Failure: LocalizedError {
    let errorDescription: String?
    init(_ message: String) { errorDescription = message }
}
@main struct MacInsertionCheckRunner {
    @MainActor static func main() async {
        do { try await checkTextInsertion() }
        catch { fputs("\(error.localizedDescription)\n", stderr); exit(1) }
    }
}
