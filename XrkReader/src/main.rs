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

    let mut result = json!({
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
    if let Some((utc_at_zero_ms, rate)) = gps_clock(&file.raw.gps_data, &gps.timecodes) {
        result["gpsUtcAtZeroMs"] = json!(utc_at_zero_ms);
        result["gpsClockRate"] = json!(rate);
    }

    serde_json::to_writer(io::stdout().lock(), &result)?;
    println!();
    Ok(())
}

/// Relates the logger clock to satellite time. Each 56-byte GPS record holds the logger timecode followed by a
/// u-blox NAV-SOL solution whose GPS week and time of week give UTC. Returns the UTC (Unix ms) at logger time 0
/// and the ratio of real elapsed time to logger elapsed time, from a least-squares fit with outliers removed.
fn gps_clock(raw: &[u8], timecodes: &[i64]) -> Option<(f64, f64)> {
    const GPS_EPOCH_UNIX_MS: f64 = 315_964_800_000.0; // 1980-01-06T00:00:00Z
    const GPS_MINUS_UTC_MS: f64 = 18_000.0; // leap seconds since the GPS epoch (unchanged since 2017)
    const WEEK_AND_TOW_VALID: u8 = 0x0C; // NAV-SOL flags: WKNSET | TOWSET

    let records = raw.len() / 56;
    if records != timecodes.len() {
        return None;
    }

    let mut pairs = Vec::with_capacity(records);
    for index in 0..records {
        let record = &raw[index * 56..index * 56 + 56];
        let itow = u32::from_le_bytes(record[4..8].try_into().ok()?) as f64;
        let ftow = i32::from_le_bytes(record[8..12].try_into().ok()?) as f64;
        let week = u16::from_le_bytes(record[12..14].try_into().ok()?) as f64;
        let fix = record[14];
        let flags = record[15];
        if fix >= 3 && week > 0.0 && flags & WEEK_AND_TOW_VALID == WEEK_AND_TOW_VALID {
            let utc = GPS_EPOCH_UNIX_MS + week * 604_800_000.0 + itow + ftow / 1e6 - GPS_MINUS_UTC_MS;
            pairs.push((timecodes[index] as f64, utc));
        }
    }

    let mut fit = fit_line(&pairs)?;
    for _ in 0..2 {
        pairs.retain(|(time, utc)| (utc - (fit.0 + fit.1 * time)).abs() <= 100.0);
        fit = fit_line(&pairs)?;
    }

    (pairs.len() >= 100 && (fit.1 - 1.0).abs() < 0.001).then_some(fit)
}

fn fit_line(pairs: &[(f64, f64)]) -> Option<(f64, f64)> {
    if pairs.len() < 2 {
        return None;
    }

    // Centre both axes so the large Unix-ms values don't swamp the slope in floating point.
    let count = pairs.len() as f64;
    let mean_time = pairs.iter().map(|pair| pair.0).sum::<f64>() / count;
    let mean_utc = pairs.iter().map(|pair| pair.1 - pairs[0].1).sum::<f64>() / count + pairs[0].1;
    let mut covariance = 0.0;
    let mut variance = 0.0;
    for (time, utc) in pairs {
        covariance += (time - mean_time) * (utc - mean_utc);
        variance += (time - mean_time) * (time - mean_time);
    }

    if variance <= 0.0 {
        return None;
    }

    let rate = covariance / variance;
    Some((mean_utc - rate * mean_time, rate))
}
