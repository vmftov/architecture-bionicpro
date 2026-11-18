CREATE DATABASE IF NOT EXISTS reports_db;

-- витрина для задания 2

CREATE TABLE IF NOT EXISTS reports_db.user_reports (
    report_date Date,
    owner_id String,
    prosthesis_id UInt32,
    prosthesis_type LowCardinality(String),
    prosthesis_description String,
    daily_avg_response_time_ms Float32,
    daily_error_count UInt32,
    daily_bending_count UInt32
) ENGINE = ReplacingMergeTree()
PARTITION BY toYYYYMM(report_date)
ORDER BY (owner_id, report_date, prosthesis_id);

-- витрина для задания 4

CREATE TABLE IF NOT EXISTS reports_db.user_reports_4 (
    report_date Date,
    owner_id String,
    prosthesis_id UInt32,
    prosthesis_type LowCardinality(String),
    prosthesis_description String,
    daily_avg_response_time_ms Float32,
    daily_error_count UInt32,
    daily_bending_count UInt32
) ENGINE = ReplacingMergeTree()
PARTITION BY toYYYYMM(report_date)
ORDER BY (owner_id, report_date, prosthesis_id);

-- orders через KafkaEngine для задания 4

CREATE TABLE IF NOT EXISTS reports_db.orders (
    id UInt32,
    order_number String,
    buyer_id String,
    prosthesis_id UInt32,
    prosthesis_type LowCardinality(String),
    prosthesis_description String,
    price Decimal(18, 2),

    is_deleted UInt8 DEFAULT 0,
    version UInt64
) ENGINE = ReplacingMergeTree(version, is_deleted)
ORDER BY (prosthesis_id, id);

CREATE TABLE IF NOT EXISTS reports_db.orders_kafka (
    message String
) ENGINE = Kafka
SETTINGS
    kafka_broker_list = 'kafka:9092',
    kafka_topic_list = 'crm.public.orders',
    kafka_group_name = 'clickhouse_orders_group',
    kafka_format = 'JSONAsString',
    kafka_num_consumers = 1,
    kafka_skip_broken_messages = 0;

CREATE MATERIALIZED VIEW IF NOT EXISTS reports_db.orders_mv TO reports_db.orders
AS
SELECT
    JSONExtractUInt(rec, 'id') AS id,
    JSONExtractString(rec, 'order_number') AS order_number,
    JSONExtractString(rec, 'buyer_id') AS buyer_id,
    JSONExtractUInt(rec, 'prosthesis_id') AS prosthesis_id,
    JSONExtractString(rec, 'prosthesis_type') AS prosthesis_type,
    JSONExtractString(rec, 'prosthesis_description') AS prosthesis_description,
    toDecimal64OrZero(JSONExtractString(rec, 'price'), 2) AS price,
    
    toUInt8(op = 'd') AS is_deleted,
    JSONExtractUInt(message, 'payload', 'ts_ms') AS version
FROM (
    SELECT
        JSONExtractString(message, 'payload', 'op') AS op,
        if (op = 'd',
            JSONExtractRaw(message, 'payload', 'before'),
            JSONExtractRaw(message, 'payload', 'after')
        ) AS rec,
        message
    FROM reports_db.orders_kafka
)
WHERE op IN ('c', 'u', 'r', 'd');

-- сгруппированная телеметрия для задания 4

CREATE TABLE IF NOT EXISTS reports_db.grouped_telemetry (
    report_date Date,
    prosthesis_id UInt32,
    daily_avg_response_time_ms Float32,
    daily_error_count UInt32,
    daily_bending_count UInt32
) ENGINE = ReplacingMergeTree()
PARTITION BY toYYYYMM(report_date)
ORDER BY (report_date, prosthesis_id);

-- MV для формирования витрины user_reports_4 для задания 4

CREATE MATERIALIZED VIEW IF NOT EXISTS reports_db.user_reports_4_mv TO reports_db.user_reports_4
AS
SELECT
    t.report_date,
    o.buyer_id AS owner_id,
    t.prosthesis_id,
    o.prosthesis_type,
    o.prosthesis_description,
    t.daily_avg_response_time_ms,
    t.daily_error_count,
    t.daily_bending_count
FROM reports_db.grouped_telemetry AS t
INNER JOIN (
    SELECT
        prosthesis_id,
        buyer_id,
        prosthesis_type,
        prosthesis_description
    FROM reports_db.orders FINAL
    WHERE prosthesis_id IN (
        SELECT DISTINCT 
            prosthesis_id
        FROM reports_db.grouped_telemetry
    )
) AS o ON o.prosthesis_id = t.prosthesis_id;
