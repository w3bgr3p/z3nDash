// StubSmokeTest.cs — проверка что DLL собирается и типы резолвятся.
// Удали после успешной сборки.

using z3n8;
using ZennoLab.CommandCenter;
using ZennoLab.InterfacesLibrary.ProjectModel;
using ZennoLab.InterfacesLibrary.Enums.Log;
using ZennoLab.InterfacesLibrary.ProjectModel.Collections;

namespace z3n8.StubSmoke;

internal static class StubSmokeTest
{
    internal static void Run()
    {
        // ── Db ─────────────────────────────────────────────────────
        var dbPg = new Db(
            mode:     dbMode.Postgre,
            pgHost:   "localhost",
            pgPort:   "5432",
            pgDbName: "postgres",
            pgUser:   "postgres",
            pgPass:   "password"
        );

        var dbSqlite = new Db(
            mode:        dbMode.SQLite,
            sqLitePath:  "test.db"
        );

        var dbFromConfig = new Db(new DbConfig());

        // ── ZennoStub ──────────────────────────────────────────────
        IZennoPosterProjectModel project = new StubProject();
        Instance instance = new Instance();

        var stub = (StubProject)project;
        stub.Db = dbPg;

        IVariableList vars   = project.Variables;
        IProfile      profile = project.Profile;

        // ── Logger ─────────────────────────────────────────────────
        var logger = new Logger(logHost: "http://localhost:5000");
        logger.Info("smoke");
        logger.Warn("smoke");
        logger.Error("smoke");

        // ── SAFU ───────────────────────────────────────────────────
        var safu = new SAFU();

        // ── Enums ──────────────────────────────────────────────────
        _ = LogType.Info;
        _ = LogColor.Default;
    }
}