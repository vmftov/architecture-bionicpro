CREATE DATABASE IF NOT EXISTS reports_db;

CREATE TABLE IF NOT EXISTS reports_db.user_reports (
    report_date Date,
    owner_id String,
    prosthesis_id UInt32,
    prosthesis_type String,
    prosthesis_description String,
    daily_avg_response_time_ms Float32,
    daily_error_count UInt32,
    daily_bending_count UInt32
) ENGINE = ReplacingMergeTree()
PARTITION BY toYYYYMM(report_date)
ORDER BY (owner_id, report_date, prosthesis_id);
