# MySQL infrastructure

MySQL 8.4 is started by `infra/docker-compose.yml`. Port 3306 is exposed only to the Compose network; it is not published on the host.

The persistent Docker volume is named `personal_fitness_planner_mysql_data`. Back up that data before migrations or destructive operational work. Application schema changes must be made through Alembic under `services/backend/alembic/versions/`, never by editing a production database manually.
