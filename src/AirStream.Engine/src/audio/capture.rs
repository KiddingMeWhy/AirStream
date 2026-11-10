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

    pub fn start(&mut self, output: SharedOutput) -> Result<(), String> {
        if self.is_running() {
            return Err("Audio capture is already running.".to_string());
        }

        let stop_flag = Arc::new(AtomicBool::new(false));
        let worker_stop_flag = Arc::clone(&stop_flag);

        let worker = thread::Builder::new()
            .name("AirStream-WASAPI-Capture".to_string())
            .spawn(move || {
                if let Err(error) = capture_loop(worker_stop_flag, output.clone()) {
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
fn capture_loop(stop_flag: Arc<AtomicBool>, output: SharedOutput) -> Result<(), String> {
    use wasapi::{
        initialize_mta, DeviceEnumerator, Direction, StreamMode,
    };

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

    audio_client
        .start_stream()
        .map_err(|error| format!("Could not start WASAPI loopback: {error}"))?;

    let sample_rate = format.get_samplespersec();
    let channels = format.get_nchannels();
    let bits_per_sample = format.get_bitspersample();
    let bytes_per_frame = format.get_blockalign() as usize;

    crate::ipc::write_message(
        &output,
        json!({
            "type": "capture_started",
            "backend": "WASAPI",
            "device": device_name,
            "sample_rate": sample_rate,
            "channels": channels,
            "bits_per_sample": bits_per_sample
        }),
    )
    .map_err(|error| format!("Could not report capture start: {error}"))?;

    let mut total_bytes: u64 = 0;
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
                total_bytes = total_bytes.saturating_add(
                    frames_read as u64 * bytes_per_frame as u64,
                );
            }
            _ => thread::sleep(Duration::from_millis(2)),
        }

        if last_report.elapsed() >= Duration::from_secs(1) {
            crate::ipc::write_message(
                &output,
                json!({
                    "type": "capture_stats",
                    "total_bytes": total_bytes,
                    "sample_rate": sample_rate,
                    "channels": channels,
                    "bits_per_sample": bits_per_sample
                }),
            )
            .map_err(|error| format!("Could not report capture statistics: {error}"))?;
            last_report = Instant::now();
        }
    }

    let _ = audio_client.stop_stream();
    Ok(())
}

#[cfg(not(windows))]
fn capture_loop(_stop_flag: Arc<AtomicBool>, _output: SharedOutput) -> Result<(), String> {
    Err("WASAPI loopback capture is only available on Windows.".to_string())
}
