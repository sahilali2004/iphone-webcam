import SwiftUI

@main
struct IPhoneWebcamApp: App {
    @StateObject private var model = AppModel()
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(model)
                .statusBarHidden()
                .persistentSystemOverlays(.hidden)
        }
        .onChange(of: scenePhase) { phase in
            model.scenePhaseChanged(phase)
        }
    }
}
