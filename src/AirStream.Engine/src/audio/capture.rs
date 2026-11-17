use serde_json::json;
use std::sync::{
    atomic::{AtomicBool, Ordering},
    Arc,
};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use crate::ipc::SharedOutput;

pub struct CaptureController {
    stop_flag: Option<Arc<AtomicBool>>,
    worker: Option<JoinHandle<()>>,
}

impl CaptureController {
    pub fn new() -> Self {
        Self {
            stop_flag: None,
            worker: None,
        }
    }

    pub fn is_running(&mut self) -> bool {
        if self.worker.as_ref().is_some_and(JoinHandle::is_finished) {
            if let Some(worker) = self.worker.take() {
                let _ = worker.join();
            }
            self.stop_flag = None;
        }

        self.worker.is_some()
    }

        pub fn start(&mut self, output: SharedOutput, pipe_name: String) -> Result<(), String> {
        if self.is_running() {
            return Err("Audio capture is already running.".to_string());
        }

        let stop_flag = Arc::new(AtomicBool::new(false));
        let worker_stop_flag = Arc::clone(&stop_flag);

        let worker = thread::Builder::new()
            .name("AirStream-WASAPI-Capture".to_string())
            .spawn(move || {
                if let Err(error) = capture_loop(worker_stop_flag, output.clone(), pipe_name) {
                    let _ = crate::ipc::write_message(
                        &output,
                        json!({
                            "type": "error",
                            "component": "audio_capture",
                            "message": error
                        }),
                    );
                }

                let _ = crate::ipc::write_message(
                    &output,
                    json!({ "type": "capture_stopped" }),
                );
            })
            .map_err(|error| format!("Could not start audio capture thread: {error}"))?;

        self.stop_flag = Some(stop_flag);
        self.worker = Some(worker);
        Ok(())
    }

    pub fn stop(&mut self) {
        if let Some(stop_flag) = self.stop_flag.take() {
            stop_flag.store(true, Ordering::Relaxed);
        }

        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

impl Drop for CaptureController {
    fn drop(&mut self) {
        self.stop();
    }
}

#[cfg(windows)]
fn capture_loop(stop_flag: Arc<AtomicBool>, output: SharedOutput, pipe_name: String) -> Result<(), String> {
    use wasapi::{
        initialize_mta, DeviceEnumerator, Direction, SampleType, StreamMode,
    };

    use crossbeam_channel::{bounded, TrySendError};

    initialize_mta()
        .ok()
        .map_err(|error| format!("Could not initialize Windows COM: {error}"))?;

    struct ComCleanup;
    impl Drop for ComCleanup {
        fn drop(&mut self) {
            wasapi::deinitialize();
        }
    }
    let _com_cleanup = ComCleanup;

    let enumerator = DeviceEnumerator::new()
        .map_err(|error| format!("Could not enumerate Windows audio devices: {error}"))?;
    let device = enumerator
        .get_default_device(Direction::Render)
        .map_err(|error| format!("Could not open the default playback device: {error}"))?;
    let device_name = device
        .get_friendlyname()
        .unwrap_or_else(|_| "Default audio device".to_string());

    let mut audio_client = device
        .get_iaudioclient()
        .map_err(|error| format!("Could not create WASAPI audio client: {error}"))?;
    let format = audio_client
        .get_mixformat()
        .map_err(|error| format!("Could not read the Windows audio format: {error}"))?;
    let (default_period, _) = audio_client
        .get_device_period()
        .map_err(|error| format!("Could not read the audio device period: {error}"))?;

    audio_client
        .initialize_client(
            &format,
            Direction::Capture,
            StreamMode::PollingShared {
                autoconvert: false,
                buffer_duration_hns: default_period,
            },
            None,
        )
        .map_err(|error| format!("Could not initialize WASAPI loopback: {error}"))?;

    let capture_client = audio_client
        .get_audiocaptureclient()
        .map_err(|error| format!("Could not get the WASAPI capture client: {error}"))?;

    let sample_rate = format.get_samplespersec();
    let channels = format.get_nchannels();
    let bits_per_sample = format.get_bitspersample();
    let bytes_per_frame = format.get_blockalign() as usize;
    let valid_bits_per_sample = format.get_validbitspersample();
    let average_bytes_per_second = format.get_avgbytespersec();
    let channel_mask = format.get_dwchannelmask();

    let sample_type = format
        .get_subformat()
        .map_err(|error| format!("Could not determine audio sample type: {error}"))?;

    let sample_type_name = match sample_type {
        SampleType::Float => "float",
        SampleType::Int => "integer",
    };

    if sample_rate == 0 || channels == 0 || bytes_per_frame == 0 {
        return Err("Windows returned an invalid audio format.".to_string());
    }

        if pipe_name.is_empty()
        || !pipe_name
            .chars()
            .all(|character| character.is_ascii_alphanumeric() || character == '_' || character == '-')
        {
            return Err("Invalid audio pipe name.".to_string());
        }

    let pipe_path = format!(r"\\.\pipe\{pipe_name}");

    let audio_pipe = std::fs::OpenOptions::new()
        .write(true)
        .open(&pipe_path)
        .map_err(|error| format!("Could not connect to the audio pipe: {error}"))?;

    const AUDIO_QUEUE_CAPACITY: usize = 8;

    let (audio_sender, audio_receiver) = bounded::<Vec<u8>>(AUDIO_QUEUE_CAPACITY);
    let drop_receiver = audio_receiver.clone();

    let writer_worker = thread::Builder::new()
        .name("AirStream-Audio-Pipe-Writer".to_string())
        .spawn(move || write_audio_pipe_packets(audio_pipe, audio_receiver))
        .map_err(|error| format!("Could not start audio pipe writer thread: {error}"))?;

    if let Err(error) = audio_client.start_stream() {
        drop(audio_sender);
        let _ = writer_worker.join();

        return Err(format!("Could not start WASAPI loopback: {error}"));
    }

    let capture_result = (|| -> Result<(), String> {
        crate::ipc::write_message(
            &output,
            json!({
                "type": "capture_started",
                "backend": "WASAPI",
                "device": device_name,
                "sample_rate": sample_rate,
                "channels": channels,
                "sample_type": sample_type_name,
                "bits_per_sample": bits_per_sample,
                "valid_bits_per_sample": valid_bits_per_sample,
                "bytes_per_frame": bytes_per_frame,
                "average_bytes_per_second": average_bytes_per_second,
                "channel_mask": channel_mask,
                "queue_capacity": AUDIO_QUEUE_CAPACITY
            }),
        )
        .map_err(|error| format!("Could not report capture start: {error}"))?;

        let mut total_bytes: u64 = 0;
        let mut dropped_packets: u64 = 0;
        let mut last_report = Instant::now();

        while !stop_flag.load(Ordering::Relaxed) {
            let next_packet = capture_client
                .get_next_packet_size()
                .map_err(|error| format!("WASAPI packet query failed: {error}"))?;

            match next_packet {
                Some(frame_count) if frame_count > 0 => {
                    let byte_count = frame_count as usize * bytes_per_frame;
                    let mut buffer = vec![0_u8; byte_count];

                    let (frames_read, _) = capture_client
                        .read_from_device(&mut buffer)
                        .map_err(|error| format!("WASAPI audio read failed: {error}"))?;

                    let data_length = frames_read as usize * bytes_per_frame;
                    buffer.truncate(data_length);

                    if !buffer.is_empty() {
                        match audio_sender.try_send(buffer) {
                            Ok(()) => {}
                            Err(TrySendError::Disconnected(_)) => {
                                return Err(
                                    "Audio pipe writer stopped unexpectedly.".to_string()
                                );
                            }
                            Err(TrySendError::Full(buffer)) => {
                                if drop_receiver.try_recv().is_ok() {
                                    dropped_packets = dropped_packets.saturating_add(1);
                                }

                                match audio_sender.try_send(buffer) {
                                    Ok(()) => {}
                                    Err(TrySendError::Full(_)) => {
                                        dropped_packets =
                                            dropped_packets.saturating_add(1);
                                    }
                                    Err(TrySendError::Disconnected(_)) => {
                                        return Err(
                                            "Audio pipe writer stopped unexpectedly.".to_string()
                                        );
                                    }
                                }
                            }
                        }
                    }

                    total_bytes = total_bytes.saturating_add(data_length as u64);
                }
                _ => thread::sleep(Duration::from_millis(2)),
            }

            if last_report.elapsed() >= Duration::from_secs(1) {
                crate::ipc::write_message(
                    &output,
                    json!({
                        "type": "capture_stats",
                        "total_bytes": total_bytes,
                        "dropped_packets": dropped_packets,
                        "queue_capacity": AUDIO_QUEUE_CAPACITY,
                        "sample_rate": sample_rate,
                        "channels": channels,
                        "sample_type": sample_type_name,
                        "bits_per_sample": bits_per_sample,
                        "valid_bits_per_sample": valid_bits_per_sample,
                        "bytes_per_frame": bytes_per_frame,
                        "average_bytes_per_second": average_bytes_per_second,
                        "channel_mask": channel_mask
                    }),
                )
                .map_err(|error| format!("Could not report capture statistics: {error}"))?;

                last_report = Instant::now();
            }
        }

        Ok(())
    })();

    let _ = audio_client.stop_stream();

    drop(audio_sender);

    let writer_result = writer_worker
        .join()
        .map_err(|_| "Audio pipe writer thread panicked.".to_string())?;

    capture_result?;
    writer_result?;

    Ok(())
}

#[cfg(windows)]
fn write_audio_pipe_packets(
    mut audio_pipe: std::fs::File,
    receiver: crossbeam_channel::Receiver<Vec<u8>>,
) -> Result<(), String> {
    use std::io::Write;

    while let Ok(buffer) = receiver.recv() {
        let frame_length = u32::try_from(buffer.len())
            .map_err(|error| format!("Invalid audio frame length: {error}"))?;

        audio_pipe
            .write_all(&frame_length.to_le_bytes())
            .and_then(|_| audio_pipe.write_all(&buffer))
            .map_err(|error| format!("Could not forward captured audio: {error}"))?;
    }

    Ok(())
}

#[cfg(not(windows))]
fn capture_loop(_stop_flag: Arc<AtomicBool>, _output: SharedOutput, _pipe_name: String) -> Result<(), String> {
    Err("WASAPI loopback capture is only available on Windows.".to_string())
}
