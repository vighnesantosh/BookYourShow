-- One PostgreSQL instance locally; on AWS these are separate Aurora clusters / databases (see design §8).
CREATE DATABASE catalog;
CREATE DATABASE inventory;
CREATE DATABASE booking;
CREATE DATABASE payment;
