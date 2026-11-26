using IPTVProxy.Data;
using IPTVProxy.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using System.Text.Json;

static void Check(bool value,string message){if(!value)throw new Exception(message);}
var tele=ChannelName.Parse("T2: 24KITCHEN ᴿᴬᵂ","NL| TELE2 VERMAAK ᴿᴬᵂ");
var delta=ChannelName.Parse("[Delta] 24Kitchen","NL | DELTA");
var hd=ChannelName.Parse("NL | 24 KITCHENʳᵉᶜ","NL | HD NEDERLAND");
Check(tele.MatchKey==delta.MatchKey && delta.MatchKey==hd.MatchKey && tele.Quality==0 && hd.Quality==720,"observed package variants");
Check(ChannelName.Parse("### TELE2 ###","NL").Separator,"separator");
Check(ChannelName.Parse("NL | MAX 1","NL | MAX PPV").MatchKey=="","event isolation");
Check(ChannelName.Parse("PLAY+: 24KITCHEN ᴴᴰ","NL| CANAL+ ONLINE VERMAAK ᴴᴰ").MatchKey==tele.MatchKey,"Canal+ prefix");
Check(ChannelName.Parse("4K | 24 KITCHEN","NL | 4ᴋ NEDERLAND").Quality==2160,"4K prefix");
Check(ChannelName.Parse("16K | ANIMAL PLANETᵁᴴᴰ","NL | 16ᴋ NEDERLANDᵁᴴᴰ ᵛᶦᵖ").Quality==0,"marketing quality unknown");
Check(ChannelName.Parse("NL: 100% NL","NL| RADIO LIVE").ChannelType=="Radio","radio");
Check(ChannelName.Parse("NL: AHSOKA ᴿᴬᵂ","NL| DISNEY+ 24/7 ᴿᴬᵂ").ChannelType=="Loop","loop");
Check(ChannelName.Parse("TEL | BABY TVᶠʰᵈ","BE | TELENETᵛᶦᵖ").Quality==1080,"superscript FHD");
Check(ChannelName.Parse("T2: GLOWFM ᴿᴬᵂ","NL| TELE2 VERMAAK ᴿᴬᵂ").ChannelType=="Radio","mixed package radio");
Check(ChannelName.Parse("[Delta] WeertFM TV","NL | DELTA").ChannelType=="Television","FM television");
Check(ChannelName.Parse("NL | DISCOVERY+ LIVE 10","NL | DISCOVERY+ SPORT").ChannelType=="Event","numbered event");
Check(ChannelName.Parse("NL: VIAPLAY TV ᴿᴬᵂ","NL| TELE2 SPORT").ChannelType=="Television","regular Viaplay");
Check(ChannelName.Parse("NL: VIAPLAY DRAMA 1 ᴿᴬᵂ","NL| FILMS HD/4K").ChannelType=="Loop","film loop");
var a=ChannelName.Parse("NL: NPO1 FHD","NL");
var b=ChannelName.Parse("NL: NPO 1 HD","NL");
Check(a.MatchKey==b.MatchKey && a.Quality==1080 && b.Quality==720,"matching quality variants");
Check(ChannelName.Parse("NL: NPO 1 Region HD","NL").MatchKey!=a.MatchKey,"regional identity");
var group=new ChannelGroup{MinimumQuality=1080};
ManagedPublication.Candidate C(int id,int priority,int quality)=>new(1,id,id,priority,1,quality,"NL","NL","Test","","");
var sources=new[]{C(1,0,0),C(2,1,1080),C(3,0,720),C(4,2,2160)};
Check(ManagedPublication.Rank(group,sources).Select(x=>x.StreamId).SequenceEqual(new[]{2,4,1}),"known first then unknown; no lower");
group.QualityFirst=true;
Check(ManagedPublication.Rank(group,sources)[0].StreamId==4,"quality first");
Check(ManagedPublication.Rank(group,sources,2)[0].StreamId==2,"manual preference");
Check(ManagedPublication.Rank(group,sources,3).All(x=>x.StreamId!=3),"preference cannot bypass minimum");
using var json=JsonDocument.Parse("""{"epg_listings":[{"id":1,"title":"VGVzdA==","start_timestamp":"1"}]}""");
Check(ProviderEpg.Parse(json.RootElement).Length==1,"EPG parser");
using var conn=new SqliteConnection("Data Source=:memory:");
await conn.OpenAsync();
var options=new DbContextOptionsBuilder<CatalogDb>().UseSqlite(conn).Options;
await using var db=new CatalogDb(options);
await db.Database.MigrateAsync();
db.Providers.Add(new Provider{Id=1,Name="Fixture"});
db.LiveCategories.Add(new LiveCategory{ProviderId=1,SourceId="1",Name="NL"});
db.LiveStreams.Add(new LiveStream{ProviderId=1,SourceId=1,Name="NL: NPO 1 FHD",CategoryId="1"});
await db.SaveChangesAsync();
await ChannelMatching.ReconcileAsync(db,CancellationToken.None);
var channel=await db.LogicalChannels.SingleAsync();
db.ChannelGroups.Add(new ChannelGroup{Name="NL",Country="NL",MinimumQuality=1080});
(await db.PublicationSettings.SingleAsync()).Managed=true;
await db.SaveChangesAsync();
var first=(await ManagedPublication.ListAsync(db,null,CancellationToken.None)).Single();
Check((await ManagedPublication.ResolveAsync(db,first.Id,CancellationToken.None)).Length==1,"resolve publication");
var stream=await db.LiveStreams.SingleAsync();
stream.Name="NL: NPO1 FHD";await db.SaveChangesAsync();
await ChannelMatching.ReconcileAsync(db,CancellationToken.None);
var second=(await ManagedPublication.ListAsync(db,null,CancellationToken.None)).Single();
Check(first.Id==second.Id && channel.Id==second.ChannelId,"stable identity on refresh");
var referenceGroup=new ChannelGroup{ReferenceCategories="17,18",MinimumQuality=1080};
var referenceSources=new[]{C(11,0,720) with {CategoryId=17,SourceOrder=4},C(12,1,1080) with {CategoryId=99,SourceOrder=1}};
Check(ManagedPublication.Eligible(referenceGroup,new LogicalChannel{Name="Test"},referenceSources,null).Single().StreamId==12,"reference membership independent of playback source");
Check(ManagedPublication.Eligible(referenceGroup,new LogicalChannel{Name="Test"},referenceSources.Skip(1),null).Length==0,"reference category required");
Check(ManagedPublication.SortKey(referenceGroup,referenceSources)==(0,0,4),"reference ordering");
Check(ArtworkService.ValidUrl("https://example.com/logo.png")&&!ArtworkService.ValidUrl("file:///tmp/logo.png")&&!ArtworkService.ValidUrl("http://localhost/logo.png")&&!ArtworkService.ValidUrl("https://user:password@example.com/logo.png"),"artwork URL validation");
Check(XmlTvGuide.Time("20261125203000 +0100")?.Hour==19,"XMLTV UTC conversion");
await db.Database.ExecuteSqlRawAsync("""
INSERT INTO VodCategories(Id,ProviderId,Kind,SourceId,Name,Selected) VALUES(1,1,'movie','10','Visible',1),(2,1,'movie','20','Hidden',0),(3,1,'series','30','Series',1);
INSERT INTO VodItems(ProviderId,Kind,SourceId,CategoryId,Name,Extension,Metadata) VALUES(1,'movie',100,1,'Gladiator','mp4','{{}}'),(1,'movie',200,2,'Gladiator II','mp4','{{}}'),(1,'series',300,3,'Gladiator Series','mp4','{{}}');
""");
var vodPublication=new VodPublication(new FixtureFactory(options));
var allTitles=await vodPublication.Search("movie","gladiator",false,0,CancellationToken.None);
Check(allTitles.Count==2&&allTitles.Count(t=>t.Published)==1,"VOD search includes unpublished titles without nullable boolean");
Check((await vodPublication.Search("movie","GLADIATOR",true,0,CancellationToken.None)).Single().Name=="Gladiator","VOD published search and case-insensitivity");
Check((await vodPublication.Search("series","gladiator",false,0,CancellationToken.None)).Count==1,"VOD search separates content types");
Check((await vodPublication.Search("movie","no matching title",false,0,CancellationToken.None)).Count==0,"VOD empty search");
var combinedTitles=await vodPublication.Search("","gladiator",false,0,CancellationToken.None);
Check(combinedTitles.Count==3&&combinedTitles.Count(t=>t.Kind=="series")==1,"VOD global search across movies and series");
Check((await vodPublication.Search("","gladiator",true,0,CancellationToken.None)).Count==2,"VOD global published search");
var ct=CancellationToken.None;
var history=new CatalogHistory(new FixtureFactory(options),new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
var states=await history.States(ct);
Check(states.Count==3&&states.Single(x=>x.Kind=="live").Hours==6&&states.Single(x=>x.Kind=="movie").Hours==24,"default per-type schedule");
await history.SetHours(1,"movie",12,ct);
await history.SetHours(1,"series",0,ct);
states=await history.States(ct);
Check(states.Single(x=>x.Kind=="movie").Hours==12&&states.Single(x=>x.Kind=="series").Hours==0,"persist schedule independently");
Check(history.TryBegin(1,"movie")&&!history.TryBegin(1,"movie")&&history.RunningSince(1,"movie")!=null,"reject duplicate active import");
history.End(1,"movie");
Check(history.RunningSince(1,"movie")==null,"release active import");
var baseline=await CatalogHistory.Snapshot(conn,null,1,"movie",ct);
Check(!CatalogHistory.Diff(baseline,baseline).Any(),"existing catalog is baseline");
using(var tx=conn.BeginTransaction())
{
    using var sql=conn.CreateCommand();sql.Transaction=tx;
    sql.CommandText="UPDATE VodItems SET Active=0 WHERE SourceId=100 AND Kind='movie'; INSERT INTO VodCategories(Id,ProviderId,Kind,SourceId,Name) VALUES(4,1,'movie','40','New category'); INSERT INTO VodItems(ProviderId,Kind,SourceId,CategoryId,Name,Extension,Metadata) VALUES(1,'movie',400,4,'New movie','mp4','{}');";
    await sql.ExecuteNonQueryAsync(ct);
    await CatalogHistory.RecordSuccess(conn,tx,1,"movie",CatalogHistory.Now(),baseline,null,ct);
    await tx.CommitAsync(ct);
}
var events=await history.Changes(1,"movie","","","",0,ct);
Check(events.Count==3&&events.Count(x=>x.Action=="added")==2&&events.Single(x=>x.Action=="removed").Name=="Gladiator","transactional add/remove history");
Check((await history.Changes(1,"movie","category","added","new",0,ct)).Single().Name=="New category","filtered category changes");
Check((await history.Changes(1,"series","","","",0,ct)).Count==0,"history isolates content types");
var latest=(await history.Runs(1,"movie",0,ct)).Single();
Check(latest.Success&&latest.Count==2&&latest.Categories==3&&latest.Added==2&&latest.Removed==1,"import totals");
var state=(await history.States(ct)).Single(x=>x.Kind=="movie");
Check(state.LastSuccess==latest.Finished&&CatalogHistory.Next(state,0)==latest.Finished+12*3600,"successful next schedule");
var beforeReturn=await CatalogHistory.Snapshot(conn,null,1,"movie",ct);
using(var tx=conn.BeginTransaction())
{
    using var sql=conn.CreateCommand();sql.Transaction=tx;sql.CommandText="UPDATE VodItems SET Active=1 WHERE SourceId=100 AND Kind='movie'";
    await sql.ExecuteNonQueryAsync(ct);
    await CatalogHistory.RecordSuccess(conn,tx,1,"movie",CatalogHistory.Now(),beforeReturn,null,ct);
    await tx.RollbackAsync(ct);
}
Check((await history.Runs(1,"movie",0,ct)).Count==1,"rolled back catalog also rolls back history");
Check(!CatalogHistory.Diff(beforeReturn,await CatalogHistory.Snapshot(conn,null,1,"movie",ct)).Any(),"rollback retains catalog");
using(var tx=conn.BeginTransaction())
{
    using var sql=conn.CreateCommand();sql.Transaction=tx;sql.CommandText="UPDATE VodItems SET Active=1 WHERE SourceId=100 AND Kind='movie'";
    await sql.ExecuteNonQueryAsync(ct);
    await CatalogHistory.RecordSuccess(conn,tx,1,"movie",CatalogHistory.Now(),beforeReturn,null,ct);
    await tx.CommitAsync(ct);
}
Check((await history.Changes(1,"movie","title","returned","",0,ct)).Single().Name=="Gladiator","returning title is not new");
await history.RecordFailure(1,"movie",CatalogHistory.Now(),"FixtureFailure",ct);
state=(await history.States(ct)).Single(x=>x.Kind=="movie");
Check(state.Success==false&&state.LastSuccess!=null&&state.LastAttempt!=null&&CatalogHistory.Next(state,0)==state.LastAttempt.Value+900,"failure retry retains last success");
Check((await history.Changes(1,"movie","","","",0,ct)).Count==4,"failure adds no phantom changes");
Console.WriteLine("Catalog checks passed.");
sealed class FixtureFactory(DbContextOptions<CatalogDb> options):IDbContextFactory<CatalogDb>
{
    public CatalogDb CreateDbContext()=>new(options);
}
