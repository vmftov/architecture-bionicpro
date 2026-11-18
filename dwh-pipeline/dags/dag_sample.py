from airflow import DAG
from airflow.operators.python import PythonOperator
from airflow.providers.postgres.hooks.postgres import PostgresHook
from airflow.providers.postgres.operators.postgres import PostgresOperator
from clickhouse_driver import Client
from datetime import datetime, timedelta, timezone
import os
import random
import logging

# Аргументы по умолчанию: владелец процесса и время отсчета для задачи
default_args = {
    'owner': 'airflow',
    'start_date': datetime(2026, 11, 14),
}

logger = logging.getLogger(__name__)


def generate_fake_telemetry_data(**context):
    """Добавляет случайную телеметрию за обрабатываемый DAG-ом день."""

    start_date = context["data_interval_start"]
    new_rows = []
    for prosthesis_id in [201, 202]:
        # отклик
        for _ in range(random.randint(5, 10)):
            new_rows.append((
                prosthesis_id,
                start_date + timedelta(minutes=random.randint(60, 60 * 20)),
                1,
                random.randint(50, 200)
            ))
        # ошибки
        for _ in range(random.randint(0, 3)):
            new_rows.append((
                prosthesis_id,
                start_date + timedelta(minutes=random.randint(60, 60 * 20)),
                2,
                random.randint(1234, 6789)
            ))
        # сгибания/разгибания
        for _ in range(random.randint(10, 20)):
            new_rows.append((
                prosthesis_id,
                start_date + timedelta(minutes=random.randint(60, 60 * 20)),
                10,
                random.random()
            ))

    if len(new_rows) > 0:
        telemetry_hook = PostgresHook(postgres_conn_id='api_db_postgres_con')
        telemetry_hook.insert_rows(
            table='public.prosthesis_telemetry',
            rows=new_rows,
            target_fields=[
                'prosthesis_id',
                'telemetry_time',
                'telemetry_type',
                'telemetry_value',
            ],
            commit_every=1000,
        )


def combine_and_save_to_clickhouse(**context):
    """Обогащает агрегаты телеметрии данными CRM и сохраняет их в ClickHouse."""

    # получаем результаты импорта из CRM
    task_instance = context['ti']
    crm_rows = task_instance.xcom_pull(task_ids='fetch_crm_data') or []
    crm_rows_dict = {row[1]: row for row in crm_rows}

    # получаем сгруппированные результаты телеметрии
    telemetry_rows = task_instance.xcom_pull(task_ids='fetch_and_group_telemetry_data') or []

    start_date = context["data_interval_start"].date()
    report_rows = []

    for prosthesis_id, avg_response_time_ms, error_count, bending_count in telemetry_rows:
        crm_row = crm_rows_dict.get(prosthesis_id)
        if crm_row is None:
            logger.warning(f"Не удалось найти запись из CRM, соответствующую протезу prosthesis_id={prosthesis_id}")
            continue

        buyer_id, _, prosthesis_type, prosthesis_description = crm_row
        report_rows.append((
            start_date,
            buyer_id,
            prosthesis_id,
            prosthesis_type,
            prosthesis_description,
            float(avg_response_time_ms),
            int(error_count),
            int(bending_count),
        ))

    if len(report_rows) == 0:
        logger.warning(f"На дату '{start_date}' не сформировано ни одного отчёта")
        return

    client = create_clickhouse_client()
    try:
        client.execute(
            """
            INSERT INTO reports_db.user_reports
            (report_date, owner_id, prosthesis_id, prosthesis_type, prosthesis_description, 
             daily_avg_response_time_ms, daily_error_count, daily_bending_count)
            VALUES
            """,
            report_rows,
        )
    finally:
        client.disconnect()


def save_telemetry_to_clickhouse(**context):
    """Сохраняет сгруппированную телеметрию в Clickhouse."""

    task_instance = context['ti']
    telemetry_rows = task_instance.xcom_pull(task_ids='fetch_and_group_telemetry_data') or []
    start_date = context["data_interval_start"].date()

    if len(telemetry_rows) == 0:
        logger.warning(f"На дату '{start_date}' не сформировано ни одного отчёта")
        return

    report_rows = []
    for prosthesis_id, avg_response_time_ms, error_count, bending_count in telemetry_rows:
        report_rows.append((
            start_date,
            prosthesis_id,
            float(avg_response_time_ms),
            int(error_count),
            int(bending_count),
        ))

    client = create_clickhouse_client()
    try:
        client.execute(
            """
            INSERT INTO reports_db.grouped_telemetry
            (report_date, prosthesis_id, daily_avg_response_time_ms, daily_error_count, daily_bending_count)
            VALUES
            """,
            report_rows,
        )
    finally:
        client.disconnect()


def create_clickhouse_client():
    return Client(
        host=os.getenv('CLICKHOUSE_HOST', 'clickhouse'),
        port=int(os.getenv('CLICKHOUSE_PORT', '9000')),
        user=os.getenv('CLICKHOUSE_USER', 'default'),
        password=os.getenv('CLICKHOUSE_PASSWORD', 'default_password'),
        database=os.getenv('CLICKHOUSE_DB', 'reports_db'),
    )


# Определяем DAG
with DAG('create_clickhouse_report',
         description="Создание отчёта в Clickhouse",
         default_args=default_args,  # аргументы по умолчанию в начале скрипта
         schedule_interval='0 0 * * *',  # запускаем каждый день в полночь
         is_paused_upon_creation=False,  # сразу запускаем
         catchup=True) as dag:  # True, чтобы проверить работоспособность

    # в проде можно чуть подождать, чтобы отстающие данные телеметрии успели прийти и записаться в БД
    # wait_all_telemetry_data_task = TimeDeltaSensor(
    #     task_id='wait_all_telemetry_data',
    #     delta=timedelta(minutes=15),
    #     mode='reschedule',
    #     poke_interval=60
    # )

    # Добавляет рандомные значения телеметрии за предыдущий день эмулируя реальное появление данных
    generate_fake_telemetry_task = PythonOperator(
        task_id='generate_fake_telemetry_data',
        python_callable=generate_fake_telemetry_data
    )

    # Задания 2 и 4- Достает и группирует данные из БД телеметрии за предыдущий день
    telemetry_data = PostgresOperator(
        task_id="fetch_and_group_telemetry_data",
        postgres_conn_id="api_db_postgres_con",
        sql="""
        SELECT prosthesis_id,
			   COALESCE(AVG(telemetry_value) FILTER (WHERE telemetry_type = 1), -1)  as daily_avg_response_time_ms,   -- Среднее время отклика за сутки
			   COUNT(telemetry_value) FILTER (WHERE telemetry_type = 2)              as daily_error_count,            -- Количество ошибок за сутки
			   COUNT(telemetry_value) FILTER (WHERE telemetry_type = 10)             as daily_bending_count           -- Количество сгибаний/разгибаний за сутки
	    FROM public.prosthesis_telemetry
	    WHERE telemetry_time >= '{{ data_interval_start.isoformat() }}' AND telemetry_time < '{{ data_interval_end.isoformat() }}'
		GROUP BY prosthesis_id;
        """,
        do_xcom_push=True
    )

    # Задание 2 - Достает данные из БД CRM
    crm_data = PostgresOperator(
        task_id='fetch_crm_data',
        postgres_conn_id="crm_db_postgres_con",
        sql="""
        SELECT buyer_id, prosthesis_id, prosthesis_type, prosthesis_description
        FROM public.orders;
        """,
        do_xcom_push=True
    )

    # Задание 4 - Сохраняет сгруппированную телеметрию в Clickhouse
    save_telemetry_to_clickhouse_task = PythonOperator(
        task_id='save_telemetry_to_clickhouse',
        python_callable=save_telemetry_to_clickhouse
    )

    # Задание 2 - Дообогащает данные телеметрии данными из CRM и сохраняет в Clickhouse
    combine_and_save_to_clickhouse_task = PythonOperator(
        task_id='combine_and_save_to_clickhouse',
        python_callable=combine_and_save_to_clickhouse
    )

    generate_fake_telemetry_task >> [telemetry_data, crm_data] >> combine_and_save_to_clickhouse_task
    telemetry_data >> save_telemetry_to_clickhouse_task

    # Тут дальше можно продолжать пайплайн
