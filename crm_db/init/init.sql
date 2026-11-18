\c crm_db;

CREATE TABLE IF NOT EXISTS public.orders (
    id SERIAL PRIMARY KEY,
    order_number VARCHAR(64) NOT NULL,
    buyer_id VARCHAR(128) NOT NULL,
    prosthesis_id INT NOT NULL UNIQUE,
    prosthesis_type VARCHAR(64) NOT NULL,
    prosthesis_description TEXT NOT NULL,
    price NUMERIC(18, 2) NOT NULL
);

-- настройки для Debezium
ALTER TABLE public.orders REPLICA IDENTITY FULL; -- чтобы при удалении Debezium видел все поля 
CREATE ROLE debezium WITH LOGIN REPLICATION PASSWORD 'debezium';
GRANT USAGE ON SCHEMA public TO debezium;
GRANT SELECT ON TABLE public.orders TO debezium;
CREATE PUBLICATION crm_publication FOR TABLE public.orders;

--
INSERT INTO public.orders 
(id, order_number, buyer_id,                                          prosthesis_id, prosthesis_type, prosthesis_description, price) 
VALUES
(1,  '10001',      '907534c7-b8de-4f08-8f40-f76da43a9557',            201,           'left-hand',     'Левая рука',           2000000.00),
(2,  '10002',      '985ae53e-52e0-4c9a-b32b-3b9e705f242b',            202,           'right-hand',    'Правая рука',          1800000.00),
(3,  '10003',      '6959023b-467c-4f32-83fe-3214109676ad',            203,           'left-leg',      'Левая нога',           2000000.00),
(4,  '10004',      'f:c8a91efc-c868-403c-851c-db7b16aa704c:john.doe', 204,           'left-hand',     'Левая рука',           2200000.00);
