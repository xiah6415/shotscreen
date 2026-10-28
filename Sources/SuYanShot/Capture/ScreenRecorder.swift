import AVFoundation
import ScreenCaptureKit

enum RecordingError: LocalizedError {
    case noFrames
    case writerFailed(String)

    var errorDescription: String? {
        switch self {
        case .noFrames: "沒有錄到任何畫面。"
        case let .writerFailed(message): "影片存檔失敗：\(message)"
        }
    }
}

/// Records a display (optionally a sub-rectangle of it) to an MP4 with ScreenCaptureKit + AVAssetWriter.
/// Pausing removes the paused stretch from the timeline. All writer state lives on `queue`.
final class ScreenRecorder: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    /// Reported on the main queue when capture stops unexpectedly.
    var onFailure: ((Error) -> Void)?

    private let queue = DispatchQueue(label: "SuYanShot.recording")
    private var stream: SCStream?
    /// Separate audio-only stream used when sound is limited to some apps (video and sound filters differ).
    private var audioStream: SCStream?
    private var writer: AVAssetWriter?
    private var videoInput: AVAssetWriterInput?
    private var systemAudioInput: AVAssetWriterInput?
    private var microphoneInput: AVAssetWriterInput?
    private var outputURL: URL?

    private var sessionStarted = false
    private var isPaused = false
    private var pausedAt = CMTime.invalid
    /// Total paused time, subtracted from every sample after a pause.
    private var offset = CMTime.zero

    /// `audioFilter`, when given, decides whose sound is recorded (e.g. a display filter including only one app);
    /// otherwise sound comes from the video filter.
    func start(filter: SCContentFilter, sourceRect: CGRect?, pixelSize: CGSize, options: RecordingOptions,
               audioFilter: SCContentFilter? = nil, to url: URL) async throws {
        // Encoders need even dimensions.
        let width = max(2, Int(pixelSize.width) / 2 * 2)
        let height = max(2, Int(pixelSize.height) / 2 * 2)

        let configuration = SCStreamConfiguration()
        if let sourceRect { configuration.sourceRect = sourceRect }
        configuration.width = width
        configuration.height = height
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(options.frameRate))
        configuration.showsCursor = options.showsCursor
        configuration.pixelFormat = kCVPixelFormatType_32BGRA
        configuration.queueDepth = 6
        let separateAudio = options.systemAudio && audioFilter != nil
        configuration.capturesAudio = options.systemAudio && !separateAudio
        configuration.excludesCurrentProcessAudio = true
        configuration.sampleRate = 48_000
        configuration.channelCount = 2
        if #available(macOS 15.0, *) { configuration.captureMicrophone = options.microphone }

        let writer = try AVAssetWriter(outputURL: url, fileType: .mp4)
        // H.264 tops out around 4096×2304; larger (5K, ultrawide Retina) needs HEVC.
        let codec: AVVideoCodecType = (width > 4096 || height > 2304) ? .hevc : .h264
        var compression: [String: Any] = [
            AVVideoAverageBitRateKey: min(max(2_000_000, width * height * options.frameRate / 10), 60_000_000),
            AVVideoExpectedSourceFrameRateKey: options.frameRate,
            AVVideoMaxKeyFrameIntervalKey: options.frameRate * 2
        ]
        if codec == .h264 { compression[AVVideoProfileLevelKey] = AVVideoProfileLevelH264HighAutoLevel }
        let video = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: codec, AVVideoWidthKey: width, AVVideoHeightKey: height,
            AVVideoCompressionPropertiesKey: compression
        ])
        video.expectsMediaDataInRealTime = true
        writer.add(video)

        func audioInput(channels: Int) -> AVAssetWriterInput {
            let input = AVAssetWriterInput(mediaType: .audio, outputSettings: [
                AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 48_000,
                AVNumberOfChannelsKey: channels, AVEncoderBitRateKey: channels * 64_000
            ])
            input.expectsMediaDataInRealTime = true
            writer.add(input)
            return input
        }
        let systemAudio = options.systemAudio ? audioInput(channels: 2) : nil
        let microphone = options.microphone ? audioInput(channels: 1) : nil
        guard writer.startWriting() else { throw RecordingError.writerFailed(writer.error?.localizedDescription ?? "無法開始寫檔") }

        let stream = SCStream(filter: filter, configuration: configuration, delegate: self)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
        if options.systemAudio && !separateAudio { try stream.addStreamOutput(self, type: .audio, sampleHandlerQueue: queue) }
        if #available(macOS 15.0, *), options.microphone { try stream.addStreamOutput(self, type: .microphone, sampleHandlerQueue: queue) }

        var audioStream: SCStream?
        if separateAudio, let audioFilter {
            let audioConfiguration = SCStreamConfiguration()
            // Video is required but unused here; keep it tiny and slow.
            audioConfiguration.width = 2
            audioConfiguration.height = 2
            audioConfiguration.minimumFrameInterval = CMTime(value: 1, timescale: 1)
            audioConfiguration.capturesAudio = true
            audioConfiguration.excludesCurrentProcessAudio = true
            audioConfiguration.sampleRate = 48_000
            audioConfiguration.channelCount = 2
            let separate = SCStream(filter: audioFilter, configuration: audioConfiguration, delegate: self)
            try separate.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue) // frames ignored below
            try separate.addStreamOutput(self, type: .audio, sampleHandlerQueue: queue)
            audioStream = separate
        }

        queue.sync {
            self.writer = writer
            self.videoInput = video
            self.systemAudioInput = systemAudio
            self.microphoneInput = microphone
            self.outputURL = url
            self.sessionStarted = false
            self.isPaused = false
            self.offset = .zero
        }
        self.stream = stream
        self.audioStream = audioStream
        try await stream.startCapture()
        try await audioStream?.startCapture()
    }

    func pause() {
        queue.async {
            guard !self.isPaused else { return }
            self.isPaused = true
            self.pausedAt = Self.now
        }
    }

    func resume() {
        queue.async {
            guard self.isPaused else { return }
            self.isPaused = false
            if self.pausedAt.isValid { self.offset = self.offset + (Self.now - self.pausedAt) }
        }
    }

    /// Finishes the file. The video runs until now (or until the pause, if paused), even if the screen stopped changing.
    func stop() async throws -> URL {
        // Take the end time before stopping the stream, which itself takes a moment.
        let stopTime = Self.now
        try? await stream?.stopCapture()
        try? await audioStream?.stopCapture()
        stream = nil
        audioStream = nil
        return try await withCheckedThrowingContinuation { continuation in
            queue.async {
                guard let writer = self.writer, let url = self.outputURL else {
                    continuation.resume(throwing: RecordingError.noFrames)
                    return
                }
                self.writer = nil
                guard self.sessionStarted else {
                    writer.cancelWriting()
                    try? FileManager.default.removeItem(at: url)
                    continuation.resume(throwing: RecordingError.noFrames)
                    return
                }
                let end = (self.isPaused ? self.pausedAt : stopTime) - self.offset
                writer.endSession(atSourceTime: end)
                [self.videoInput, self.systemAudioInput, self.microphoneInput].forEach { $0?.markAsFinished() }
                writer.finishWriting {
                    if writer.status == .completed {
                        continuation.resume(returning: url)
                    } else {
                        continuation.resume(throwing: RecordingError.writerFailed(writer.error?.localizedDescription ?? "未知錯誤"))
                    }
                }
            }
        }
    }

    /// Stops and deletes the partial file.
    func cancel() async {
        try? await stream?.stopCapture()
        try? await audioStream?.stopCapture()
        stream = nil
        audioStream = nil
        await withCheckedContinuation { continuation in
            queue.async {
                self.writer?.cancelWriting()
                self.writer = nil
                if let url = self.outputURL { try? FileManager.default.removeItem(at: url) }
                continuation.resume()
            }
        }
    }

    // MARK: - SCStreamOutput / SCStreamDelegate

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard sampleBuffer.isValid, let writer, writer.status == .writing, !isPaused else { return }
        switch type {
        case .screen:
            guard stream === self.stream else { return } // the audio-only stream's placeholder frames
            // Idle/blank frames carry no new image.
            guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
                  let rawStatus = attachments.first?[.status] as? Int, SCFrameStatus(rawValue: rawStatus) == .complete else { return }
            if !sessionStarted {
                writer.startSession(atSourceTime: sampleBuffer.presentationTimeStamp)
                sessionStarted = true
            }
            append(sampleBuffer, to: videoInput)
        case .audio:
            if sessionStarted { append(sampleBuffer, to: systemAudioInput) }
        default:
            if #available(macOS 15.0, *), type == .microphone, sessionStarted { append(sampleBuffer, to: microphoneInput) }
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        DispatchQueue.main.async { self.onFailure?(error) }
    }

    private func append(_ sampleBuffer: CMSampleBuffer, to input: AVAssetWriterInput?) {
        guard let input, input.isReadyForMoreMediaData else { return }
        let buffer = offset == .zero ? sampleBuffer : retimed(sampleBuffer, by: offset)
        if let buffer { input.append(buffer) }
    }

    private func retimed(_ sampleBuffer: CMSampleBuffer, by offset: CMTime) -> CMSampleBuffer? {
        var count: CMItemCount = 0
        CMSampleBufferGetSampleTimingInfoArray(sampleBuffer, entryCount: 0, arrayToFill: nil, entriesNeededOut: &count)
        var timing = [CMSampleTimingInfo](repeating: CMSampleTimingInfo(), count: count)
        CMSampleBufferGetSampleTimingInfoArray(sampleBuffer, entryCount: count, arrayToFill: &timing, entriesNeededOut: &count)
        for index in timing.indices {
            timing[index].presentationTimeStamp = timing[index].presentationTimeStamp - offset
            if timing[index].decodeTimeStamp.isValid { timing[index].decodeTimeStamp = timing[index].decodeTimeStamp - offset }
        }
        var output: CMSampleBuffer?
        CMSampleBufferCreateCopyWithNewTiming(allocator: nil, sampleBuffer: sampleBuffer, sampleTimingEntryCount: count,
                                              sampleTimingArray: &timing, sampleBufferOut: &output)
        return output
    }

    /// ScreenCaptureKit timestamps use the host clock.
    private static var now: CMTime { CMClockGetTime(CMClockGetHostTimeClock()) }
}
