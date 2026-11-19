-- One-time setup so the containerized API can reach the host's SQL Server with SQL authentication.
-- Prerequisites: SQL Server has mixed-mode authentication and TCP/IP enabled.
--
-- Run with:  sqlcmd -S localhost -E -C -v AppPassword="<a strong password>" -i docker\create-sql-login.sql
-- then put the same password in docker/.env (DOCKERDBCONNECTIONSTRING).

IF DB_ID(N'MapleLedger') IS NULL
    CREATE DATABASE MapleLedger;
GO

IF SUSER_ID(N'mapleledger_app') IS NULL
    CREATE LOGIN mapleledger_app WITH PASSWORD = N'$(AppPassword)', CHECK_POLICY = ON;
GO

USE MapleLedger;
GO

IF USER_ID(N'mapleledger_app') IS NULL
    CREATE USER mapleledger_app FOR LOGIN mapleledger_app;
GO

-- db_owner because the API applies EF Core migrations on startup. To run with less privilege,
-- set Database__MigrateOnStartup=false, apply migrations by hand, and grant db_datareader/db_datawriter instead.
ALTER ROLE db_owner ADD MEMBER mapleledger_app;
GO
