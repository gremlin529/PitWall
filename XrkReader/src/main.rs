use std::env;
use std::error::Error;
use std::io;

use libxrk::{parser::ChannelValues, read_xrk_file};
use serde_json::json;

fn main() -> Result<(), Box<dyn Error>> {
    let mut arguments = env::args_os();
    let _program = arguments.next();
    let file_path = arguments
        .next()
        .ok_or("Usage: XrkReader.exe <file.xrk>")?;
    if arguments.next().is_some() {
        return Err("Expected one XRK file path.".into());
    }

    let file = read_xrk_file(file_path)?;
    let gps = file
        .gps
        .as_ref()
        .ok_or("The XRK file does not contain GPS telemetry.")?;

    let sample_count = gps.timecodes.len();
    if [
        gps.speed.len(),
        gps.latitude.len(),
        gps.longitude.len(),
        gps.inline_acc.len(),
        gps.lateral_acc.len(),
    ]
    .iter()
    .any(|length| *length != sample_count)
    {
        return Err("The XRK GPS channels have inconsistent sample counts.".into());
    }

    let laps: Vec<_> = file
        .laps
        .iter()
        .map(|lap| {
            json!({
                "number": lap.num,
                "startTimeMs": lap.start_time,
                "endTimeMs": lap.end_time,
                "type": lap.lap_type
            })
        })
        .collect();
    let samples: Vec<_> = (0..sample_count)
        .map(|index| {
            json!({
                "timeMs": gps.timecodes[index],
                "latitude": gps.latitude[index],
                "longitude": gps.longitude[index],
                "speedMps": gps.speed[index],
                "longitudinalG": gps.inline_acc[index],
                "lateralG": gps.lateral_acc[index]
            })
        })
        .collect();
    let channels: Vec<_> = file
        .channels
        .iter()
        .filter_map(|channel| {
            let values: Vec<f64> = match &channel.values {
                ChannelValues::UInt8(values) => values.iter().map(|value| *value as f64).collect(),
                ChannelValues::UInt16(values) => values.iter().map(|value| *value as f64).collect(),
                ChannelValues::Int16(values) => values.iter().map(|value| *value as f64).collect(),
                ChannelValues::Int32(values) => values.iter().map(|value| *value as f64).collect(),
                ChannelValues::UInt32(values) => values.iter().map(|value| *value as f64).collect(),
                ChannelValues::Float32(values) => values.iter().map(|value| *value as f64).collect(),
                ChannelValues::Float64(values) => values.clone(),
            };
            let channel_samples: Vec<_> = channel
                .timecodes
                .iter()
                .zip(values.iter())
                .filter(|(_, value)| value.is_finite())
                .map(|(time_ms, value)| json!([time_ms, value]))
                .collect();
            if channel_samples.is_empty() {
                return None;
            }
            Some(json!({
                "name": channel.name,
                "units": channel.units,
                "samples": channel_samples
            }))
        })
        .collect();

    let result = json!({
        "driver": file.metadata.driver,
        "vehicle": file.metadata.vehicle,
        "venue": file.metadata.venue,
        "logDate": file.metadata.log_date,
        "logTime": file.metadata.log_time,
        "session": file.metadata.session,
        "laps": laps,
        "channels": channels,
        "samples": samples
    });

    serde_json::to_writer(io::stdout().lock(), &result)?;
    println!();
    Ok(())
}
