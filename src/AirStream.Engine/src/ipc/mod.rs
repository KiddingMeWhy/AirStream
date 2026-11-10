use serde::Deserialize;
use serde_json::{json, Value};
use std::io::{self, BufWriter, Write};
use std::sync::{Arc, Mutex};

pub type SharedOutput = Arc<Mutex<BufWriter<io::Stdout>>>;

#[derive(Debug, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum Command {
    Ping { request_id: Option<String> },
    StartCapture { pipe_name: String },
    StopCapture,
    Shutdown,
}

pub fn write_message(output: &SharedOutput, message: Value) -> io::Result<()> {
    let mut output = output
        .lock()
        .map_err(|_| io::Error::other("IPC output lock was poisoned"))?;
    serde_json::to_writer(&mut *output, &message)?;
    output.write_all(b"\n")?;
    output.flush()
}

pub fn write_error(output: &SharedOutput, message: impl Into<String>) -> io::Result<()> {
    write_message(output, json!({ "type": "error", "message": message.into() }))
}
