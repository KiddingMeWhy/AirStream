mod audio;
mod ipc;

use audio::CaptureController;
use ipc::{Command, SharedOutput};
use serde_json::json;
use std::io::{self, BufRead, BufWriter};
use std::sync::{Arc, Mutex};

fn main() -> io::Result<()> {
    // stdout is reserved exclusively for the JSON-lines IPC protocol.
    let output: SharedOutput = Arc::new(Mutex::new(BufWriter::new(io::stdout())));
    let stdin = io::stdin();
    let mut capture = CaptureController::new();

    ipc::write_message(
        &output,
        json!({
            "type": "engine_ready",
            "engine": "AirStream.Engine",
            "version": env!("CARGO_PKG_VERSION")
        }),
    )?;

    for line in stdin.lock().lines() {
        let line = match line {
            Ok(line) => line,
            Err(error) => {
                eprintln!("IPC input error: {error}");
                break;
            }
        };

        if line.trim().is_empty() {
            continue;
        }

        let command = match serde_json::from_str::<Command>(&line) {
            Ok(command) => command,
            Err(error) => {
                ipc::write_error(&output, format!("Invalid command: {error}"))?;
                continue;
            }
        };

        match command {
            Command::Ping { request_id } => {
                ipc::write_message(
                    &output,
                    json!({
                        "type": "pong",
                        "request_id": request_id,
                        "engine_version": env!("CARGO_PKG_VERSION")
                    }),
                )?;
            }
            Command::StartCapture => {
                match capture.start(Arc::clone(&output)) {
                    Ok(()) => ipc::write_message(
                        &output,
                        json!({ "type": "capture_starting" }),
                    )?,
                    Err(error) => ipc::write_error(&output, error)?,
                }
            }
            Command::StopCapture => {
                capture.stop();
                ipc::write_message(&output, json!({ "type": "capture_stop_requested" }))?;
            }
            Command::Shutdown => {
                capture.stop();
                ipc::write_message(&output, json!({ "type": "engine_stopping" }))?;
                break;
            }
        }
    }

    capture.stop();
    eprintln!("AirStream.Engine stopped.");
    Ok(())
}
