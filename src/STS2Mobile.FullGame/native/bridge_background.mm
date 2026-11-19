// Keeps the app running in the background while a Steam bridge session is open, so switching apps does not end a
// multiplayer game.
//
// iOS suspends a backgrounded app within seconds, which ends its connection to the bridge server. An app that plays
// audio keeps running, so with UIBackgroundModes audio (tools/prepare_full_game_xcode.py) this plays silence through a
// mixable Playback session until the app returns, the session ends or the player force-quits the app. Mixing keeps
// music the player starts elsewhere playing. The managed host (STS2Mobile.Platform/BridgeBackground.cs, used by
// STS2Mobile.GameHost/SteamBridgeMenu.cs) looks the sts2_bridge_background_* functions up by name and calls them on the
// main thread when the app enters and leaves the background.
//
// - A short background task covers the switch itself: the session can only start while the app still runs.
// - An interruption (a call) or a route change stops the engine; it starts again once that is over.
// - On return the session goes back to the category the app had, for the game's own sound.
//
// Changing the audio session takes a while, so the session and the engine are only touched on a serial queue of their
// own. The background task is only touched on the main thread.
#import <AVFoundation/AVFoundation.h>
#import <UIKit/UIKit.h>

namespace {

dispatch_queue_t gQueue;
AVAudioEngine *gEngine;
id gInterruptionObserver;
id gConfigurationObserver;
AVAudioSessionCategory gSavedCategory;
AVAudioSessionMode gSavedMode;
AVAudioSessionCategoryOptions gSavedOptions = 0;
bool gHolding = false;
UIBackgroundTaskIdentifier gTask = UIBackgroundTaskInvalid;

dispatch_queue_t Queue() {
	static dispatch_once_t once;
	dispatch_once(&once, ^{
		gQueue = dispatch_queue_create("io.github.sts2mobile.bridge-background", DISPATCH_QUEUE_SERIAL);
	});
	return gQueue;
}

void EndTask() {
	if (gTask != UIBackgroundTaskInvalid) {
		[UIApplication.sharedApplication endBackgroundTask:gTask];
		gTask = UIBackgroundTaskInvalid;
	}
}

bool StartEngine(NSError **error) {
	if (gEngine == nil) {
		gEngine = [[AVAudioEngine alloc] init];
		AVAudioFormat *format = [[AVAudioFormat alloc] initStandardFormatWithSampleRate:44100 channels:2];
		// Silence: the audio only has to run, never to be heard.
		AVAudioSourceNode *silence = [[AVAudioSourceNode alloc] initWithFormat:format
				renderBlock:^OSStatus(BOOL *isSilence, const AudioTimeStamp *timestamp, AVAudioFrameCount frames, AudioBufferList *output) {
			for (UInt32 i = 0; i < output->mNumberBuffers; i++) {
				memset(output->mBuffers[i].mData, 0, output->mBuffers[i].mDataByteSize);
			}
			return noErr;
		}];
		[gEngine attachNode:silence];
		[gEngine connect:silence to:gEngine.mainMixerNode format:format];
	}
	if (gEngine.isRunning) {
		return true;
	}
	[gEngine prepare];
	return [gEngine startAndReturnError:error];
}

// On the queue: start the engine again if the session is still held.
void Restart() {
	dispatch_async(Queue(), ^{
		if (!gHolding) {
			return;
		}
		NSError *error = nil;
		if (![AVAudioSession.sharedInstance setActive:YES error:&error] || !StartEngine(&error)) {
			NSLog(@"Steam bridge background audio did not restart: %@", error.localizedDescription);
		}
	});
}

// On the queue.
bool StartHolding() {
	if (gHolding) {
		return true;
	}
	AVAudioSession *session = AVAudioSession.sharedInstance;
	gSavedCategory = session.category;
	gSavedMode = session.mode;
	gSavedOptions = session.categoryOptions;
	NSError *error = nil;
	if (![session setCategory:AVAudioSessionCategoryPlayback mode:AVAudioSessionModeDefault
			options:AVAudioSessionCategoryOptionMixWithOthers error:&error] ||
			![session setActive:YES error:&error] || !StartEngine(&error)) {
		NSLog(@"Steam bridge background audio unavailable: %@", error.localizedDescription);
		[gEngine stop];
		[session setCategory:gSavedCategory mode:gSavedMode options:gSavedOptions error:nil];
		return false;
	}
	if (gInterruptionObserver == nil) {
		gInterruptionObserver = [NSNotificationCenter.defaultCenter addObserverForName:AVAudioSessionInterruptionNotification
				object:nil queue:nil usingBlock:^(NSNotification *note) {
			NSNumber *type = note.userInfo[AVAudioSessionInterruptionTypeKey];
			if (type.unsignedIntegerValue == AVAudioSessionInterruptionTypeEnded) {
				Restart();
			}
		}];
		gConfigurationObserver = [NSNotificationCenter.defaultCenter addObserverForName:AVAudioEngineConfigurationChangeNotification
				object:gEngine queue:nil usingBlock:^(NSNotification *note) {
			Restart();
		}];
	}
	gHolding = true;
	return true;
}

// On the queue.
void StopHolding() {
	if (!gHolding) {
		return;
	}
	gHolding = false;
	[gEngine stop];
	[AVAudioSession.sharedInstance setCategory:gSavedCategory mode:gSavedMode options:gSavedOptions error:nil];
}

}  // namespace

// 1 when silent audio keeps the app running, 0 when only the short background task does.
extern "C" __attribute__((visibility("default"))) int sts2_bridge_background_begin(void) {
	if (gTask == UIBackgroundTaskInvalid) {
		gTask = [UIApplication.sharedApplication beginBackgroundTaskWithName:@"Steam bridge" expirationHandler:^{
			EndTask();
		}];
	}
	__block bool held = false;
	dispatch_sync(Queue(), ^{
		held = StartHolding();
	});
	if (held) {
		EndTask();
	}
	return held ? 1 : 0;
}

extern "C" __attribute__((visibility("default"))) void sts2_bridge_background_end(void) {
	EndTask();
	dispatch_async(Queue(), ^{
		StopHolding();
	});
}

// Holding the functions here keeps the linker from removing them: nothing in the app calls them
// directly, the managed host finds them by name.
__attribute__((used)) static void *volatile gBridgeBackgroundExports[] = {
	(void *)&sts2_bridge_background_begin, (void *)&sts2_bridge_background_end
};
