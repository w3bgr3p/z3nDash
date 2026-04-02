#r "W:\code_hard\.net\z3nIO\z3nStub\z3nStub\bin\Debug\net8.0-windows\z3nIO.Stub.dll"
#r "nuget: Newtonsoft.Json, 13.0.4"
#r "nuget: Npgsql, 10.0.1"
#r "nuget: Nethereum.Web3, 4.29.0"
#r "nuget: Nethereum.Signer, 4.29.0"
#r "nuget: Microsoft.Extensions.Configuration, 8.0.0"
#r "nuget: Microsoft.Extensions.Configuration.Json, 8.0.0"

using z3nIO;

var db = new Db(
    mode:         dbMode.Postgre,
    pgHost:       "localhost",
    pgPort:       "5432",
    pgDbName:     "postgres",
    pgUser:       "postgres",
    pgPass:       "baracuda69",
    defaultTable: "__MarketMavericks"
);

var project = new StubProject();
project.Db = db;

var mm = new MarketMavericks(project, new ZennoLab.CommandCenter.Instance());
mm.RunDaily();