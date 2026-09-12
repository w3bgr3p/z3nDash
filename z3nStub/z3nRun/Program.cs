using z3nDash;
using ZennoLab.InterfacesLibrary.ProjectModel;

var db = new Db(
    mode: dbMode.Postgre,
    pgHost: "localhost",
    pgPort: "5432",
    pgDbName: "postgres",
    pgUser: "postgres",
    pgPass: "baracuda69",
    defaultTable: "__MarketMavericks"
);

var project = new StubProject();
project.Db = db;
project.Var("dbSource", db.Source);
project.Variables["acc0"].Value = "1";
var mm = new MarketMavericks(project, new ZennoLab.CommandCenter.Instance());
mm.RunDaily();