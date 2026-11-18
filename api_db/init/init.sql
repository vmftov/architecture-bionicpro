\c api_db;

CREATE TABLE IF NOT EXISTS public.prosthesis_telemetry (
    id SERIAL PRIMARY KEY,
    prosthesis_id INT NOT NULL,
    telemetry_time TIMESTAMPTZ NOT NULL,
    telemetry_type INT NOT NULL,
    telemetry_value DOUBLE PRECISION NOT NULL
);

CREATE INDEX IF NOT EXISTS idx__prosthesis_telemetry__telemetry_time__telemetry_type__prosthesis_id 
ON public.prosthesis_telemetry (telemetry_time, telemetry_type, prosthesis_id);

-- фиктивные данные добавляются прямо в DWH-пайплайне
 