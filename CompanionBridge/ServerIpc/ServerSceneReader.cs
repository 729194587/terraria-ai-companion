using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ObjectData;
using CompanionBridge.Common.Systems;
using CompanionBridge.Motion;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Called by the existing bound query adapter on the original game thread/Gate.
internal static class ServerSceneReader
{
    private static long sequence;
    private static readonly HashSet<int> Ores=new(){TileID.Copper,TileID.Tin,TileID.Iron,TileID.Lead,TileID.Silver,TileID.Tungsten,
        TileID.Gold,TileID.Platinum,TileID.Demonite,TileID.Crimtane,TileID.Meteorite,TileID.Hellstone,TileID.Cobalt,TileID.Palladium,
        TileID.Mythril,TileID.Orichalcum,TileID.Adamantite,TileID.Titanium,TileID.Chlorophyte};
    internal static object Read(Player p,AiReadOnlyProtocol.Identity identity,JsonElement args)
    {
        var layers=SceneLayers.Read(args);
        long started=Stopwatch.GetTimestamp();ulong tick=Main.GameUpdateCount;
        string sceneId=identity.World+":scene:"+(++sequence);
        int Cap(string key,int fallback)=>args.TryGetProperty(key,out var n)?n.GetInt32():fallback;
        int objectCap=Cap("maxObjects",16),landingCap=Cap("maxRegions",12),placementCap=Cap("maxPlacements",12),hazardCap=Cap("maxHazards",24);
        int ox=(int)Math.Floor(p.Center.X/16)-22,oy=(int)Math.Floor(p.Bottom.Y/16)-17;
        var cells=new SceneCell[45,29];var anchors=new Dictionary<(int,int),object>();var content=new Dictionary<int,string>();
        string Key(int type){if(!content.TryGetValue(type,out var key))content[type]=key=TileLoader.GetTile(type)?.FullName??"Terraria/"+TileID.Search.GetName(type);return key;}
        int unknown=0,uninterpreted=0,unknownNavigation=0;
        for(int y=0;y<29;y++)for(int x=0;x<45;x++)
        {
            var c=ObservationReader.ReadCell(x+ox,y+oy,anchors);
            if(c.Knowledge!="read")unknown++;
            if(c.CollisionKnowledge!="known")uninterpreted++;
            string kind=null;int? ax=null,ay=null;var sources=new List<string>();
            if(c.LiquidAmount>0)sources.Add(c.LiquidType switch{0=>"water",1=>"lava",2=>"honey",3=>"shimmer",_=>"unknown_liquid"});
            if(c.HasTile&&!c.Actuated)
            {
                if(c.TouchDamage>0)sources.Add("touch_damage");if(c.Hot)sources.Add("touch_hot");
                if(c.Bleeding)sources.Add("touch_bleeding");if(c.DestroyOnTouch)sources.Add("touch_destroy");if(c.Boulder)sources.Add("boulder_tile");
                int type=c.TileType??-1;
                if(type>=0&&(TileID.Sets.IceSkateSlippery[type]||type is TileID.HoneyBlock or TileID.Cobweb or TileID.Asphalt or TileID.SlimeBlock or TileID.FrozenSlimeBlock))sources.Add("unsupported_motion_surface");
            }
            if((layers.Has("map")||layers.Has("objects"))&&c.HasTile&&c.TileType is int t)
            {
                if(t<TileID.Count&&TileID.Sets.IsATreeTrunk[t])kind="tree_component";
                else if(Ores.Contains(t))kind="ore_component";
                else if(t is TileID.Containers or TileID.Containers2 or TileID.Dressers)kind="chest";
                else if(t is TileID.OpenDoor or TileID.ClosedDoor)kind="door";
                else if(t==TileID.WorkBenches)kind="workbench";
                else if(t==TileID.Torches)kind="light_source";
                if(layers.Has("objects")&&kind is ("chest" or "door" or "workbench"))
                {var a=TileObjectData.TopLeft(x+ox,y+oy);ax=a.X;ay=a.Y;}
            }
            char nav=TerrainReader.ReadCell(x+ox,y+oy);
            if(nav=='?')unknownNavigation++;
            if(sources.Contains("unsupported_motion_surface"))nav='!';
            bool placementFacts=layers.Has("objectPlacementCandidates")&&c.TileType is int placementType&&placementType>=0&&placementType<TileID.Count;
            cells[x,y]=new(c,nav,kind,(layers.Has("objects")||layers.Has("hazards"))&&c.TileType is int material&&(kind!=null||sources.Count>0)?Key(material):null,ax,ay,sources.ToArray(),
                placementFacts&&Main.tileCut[c.TileType.Value],placementFacts&&TileID.Sets.BreakableWhenPlacing[c.TileType.Value]);
        }
        var actor=new WorldRect(p.position.X,p.position.Y,p.width,p.height);
        bool Intersects(Entity e)=>e.position.X<(ox+45)*16&&e.position.X+e.width>ox*16&&e.position.Y<(oy+29)*16&&e.position.Y+e.height>oy*16;
        var bodies=layers.Has("placementCandidates")||layers.Has("objectPlacementCandidates")?Main.player.Where(other=>other.active&&!other.dead&&!other.ghost&&Intersects(other)).Select(other=>new WorldRect(other.position.X,other.position.Y,other.width,other.height)).ToList():new List<WorldRect>();
        if(!bodies.Contains(actor))bodies.Add(actor);
        var objects=layers.Has("objects")?SceneGeometry.Objects(cells,ox,oy).ToList():new List<SceneObject>();
        var dynamicHazards=new List<(float Distance,object Value)>();
        float HazardDistance(Entity e)=>Math.Abs(e.Center.X-p.Center.X)+Math.Abs(e.Center.Y-p.Center.Y);int entitiesScanned=0;
        var entityMarkers=new List<(int,int,char)>();
        void EntityObject(Entity e,string kind,int? type,string key,object detail,char glyph)
        {
            int cx=(int)Math.Floor((e.position.X+e.width/2f)/16),cy=(int)Math.Floor((e.position.Y+e.height/2f)/16);
            if(layers.Has("objects"))objects.Add(new("e"+(++entitiesScanned),kind,type,key,new(cx,cy,1,1),1,false,
                new{boundsWorldPixels=new WorldRect(e.position.X,e.position.Y,e.width,e.height),velocity=new StateVector(e.velocity.X,e.velocity.Y),detail}));
            if(layers.Has("map"))entityMarkers.Add((Math.Clamp(cx-ox,0,44),Math.Clamp(cy-oy,0,28),glyph));
        }
        if(layers.Has("map")||layers.Has("objects")||layers.Has("hazards"))foreach(var n in Main.npc)if(n.active&&Intersects(n))
        {
            var evidence=new NpcEvidence(n.friendly,n.townNPC||NPCID.Sets.ActsLikeTownNPC[n.type],NPCID.Sets.CountsAsCritter[n.type],n.damage,n.CanBeChasedBy(),n.ModNPC!=null,NPCID.Sets.ProjectileNPC[n.type]);
            var classification=EntityCollector.Classify(evidence);
            string entityRef=identity.World+":"+identity.Generation+":npc:"+n.whoAmI+":"+n.GetGlobalNPC<AtomicNpcIdentity>().Token;
            EntityObject(n,"npc",n.type,n.ModNPC?.FullName??"Terraria/"+NPCID.Search.GetName(n.type),new{slot=n.whoAmI,entityRef,n.life,n.damage,classification=classification.Kind,basis=classification.Basis},classification.Kind=="hostile_creature"?'!':'N');
            if(layers.Has("hazards")&&classification.Kind is ("hostile_creature" or "unknown"))dynamicHazards.Add((HazardDistance(n),new{kind="npc",slot=n.whoAmI,entityRef,classification=classification.Kind,boundsWorldPixels=new WorldRect(n.position.X,n.position.Y,n.width,n.height),n.damage,knowledge="inferred classification; no current threat or path prediction"}));
        }
        if(layers.Has("map")||layers.Has("objects"))for(int i=0;i<Main.item.Length;i++)
        {var item=Main.item[i];if(item.active&&!item.IsAir&&Intersects(item))EntityObject(item,"drop",item.type,item.ModItem?.FullName??"Terraria/"+ItemID.Search.GetName(item.type),new{slot=i,item.stack,pickup="not evaluated"},'$');}
        if(layers.Has("map")||layers.Has("objects"))foreach(var other in Main.player)if(other.active&&!ReferenceEquals(other,p)&&Intersects(other))EntityObject(other,"player",null,"Terraria/Player",new{slot=other.whoAmI,other.dead,other.ghost},'P');
        if(layers.Has("map")||layers.Has("hazards"))foreach(var projectile in Main.projectile)if(projectile.active&&projectile.hostile&&Intersects(projectile))
        {
            if(layers.Has("hazards"))dynamicHazards.Add((HazardDistance(projectile),new{kind="hostile_projectile",slot=projectile.whoAmI,typeId=projectile.type,projectile.damage,
                boundsWorldPixels=new WorldRect(projectile.position.X,projectile.position.Y,projectile.width,projectile.height),velocity=new StateVector(projectile.velocity.X,projectile.velocity.Y),knowledge="current hostile flag; no hit/trajectory prediction"}));
            if(layers.Has("map"))entityMarkers.Add((Math.Clamp((int)(projectile.Center.X/16)-ox,0,44),Math.Clamp((int)(projectile.Center.Y/16)-oy,0,28),'*'));
        }
        int actorX=Math.Clamp((int)(p.Center.X/16)-ox,0,44),actorY=Math.Clamp((int)(p.Center.Y/16)-oy,0,28);
        var geometry=new Lazy<string[]>(()=>SceneGeometry.GeometryRows(cells));
        var response=new Dictionary<string,object>{
            ["schemaVersion"]=2,["sceneId"]=sceneId,["gameUpdate"]=tick,
            ["bounds"]=new{minX=ox,minY=oy,maxX=ox+44,maxY=oy+28},["tileSize"]=16,
            ["actor"]=new{slot=p.whoAmI,boundsWorldPixels=actor,feetY=p.Bottom.Y,velocity=new StateVector(p.velocity.X,p.velocity.Y),p.dead,
                navigationProfileSupported=GameNavigation.Ordinary(p),mapTile=new{x=ox+actorX,y=oy+actorY},
                bodyTileBounds=BodyTileBounds.FromPixels(p.position.X,p.position.Y,p.width,p.height)},
            ["coverage"]=new{scannedTiles=1305,unknownCells=unknown,uninterpretedCollisionCells=uninterpreted,unknownNavigationCells=unknownNavigation,geometryComplete=unknown==0&&uninterpreted==0&&unknownNavigation==0},
            ["limits"]="observation-scoped IDs; no visibility/lighting, contents, persistent map or custom Mod hook interpretation; truncation is per collection, never empty=absent",
            ["includedLayers"]=layers.Included};
        layers.Add(response,"map",()=>new{rows=SceneGeometry.MapRows(cells,geometry.Value,actorX,actorY,entityMarkers),originX=ox,originY=oy});
        layers.Add(response,"geometry",()=>new{rows=geometry.Value,originX=ox,originY=oy});
        layers.Add(response,"objects",()=>
        {
            float Distance(SceneObject o)=>Math.Abs((o.ObservedBounds.X+o.ObservedBounds.Width/2f)*16-p.Center.X)+Math.Abs((o.ObservedBounds.Y+o.ObservedBounds.Height/2f)*16-p.Center.Y);
            var result=SceneGeometry.LimitObjects(objects,objectCap,Distance);
            response["objectCounts"]=new{observed=objects.GroupBy(o=>o.Kind).ToDictionary(g=>g.Key,g=>g.Count()),returned=result.Items.GroupBy(o=>o.Kind).ToDictionary(g=>g.Key,g=>g.Count())};
            return result;
        });
        layers.Add(response,"hazards",()=>
        {
            var tileHazards=SceneGeometry.Hazards(cells,ox,oy).OrderBy(h=>Math.Max(0,Math.Max(h.MinX*16-p.Center.X,p.Center.X-(h.MaxX+1)*16))+Math.Abs(h.Y*16+8-p.Center.Y)).ThenBy(h=>h.Y).ThenBy(h=>h.MinX);
            return new{tiles=SceneGeometry.Limit(tileHazards,hazardCap),entities=SceneGeometry.Limit(dynamicHazards.OrderBy(h=>h.Distance).Select(h=>h.Value),hazardCap)};
        });
        layers.Add(response,"landingRegions",()=>
        {
            var world=new NativeMotionWorld(new(p.position.X,p.position.Y,p.velocity.X,p.velocity.Y,TerrainReader.Read(p).Grounded));
            var landings=p.width==20&&p.height==42?SceneGeometry.Landings(cells,ox,oy,p.position.X,p.Bottom.Y,world.SafeFlatLanding):Array.Empty<SceneLanding>();
            return SceneGeometry.LimitLandings(landings,landingCap);
        });
        layers.Add(response,"placementCandidates",()=>
        {
            response["placementMaterial"]=new{itemType=p.HeldItem.type,p.HeldItem.stack,p.HeldItem.createTile,hotbarKey=HotbarSlots.KeyForInventorySlot(p.selectedItem),supported=ActorPlacement.Supported(p.HeldItem)};
            return SceneGeometry.LimitPlacements(SceneGeometry.Placements(cells,ox,oy,actor,bodies),placementCap);
        });
        layers.Add(response,"objectPlacementCandidates",()=>
        {
            var bench=ActorObjectPlacement.Definition();
            var placements=SceneGeometry.ObjectPlacements(cells,ox,oy,actor,bodies,
                bench?.Width??0,bench?.Height??0,bench?.OriginX??0,bench?.OriginY??0,bench?.SupportStart??0,bench?.SupportCount??0,out var diagnostics);
            response["objectPlacementMaterial"]=new{itemType=p.HeldItem.type,p.HeldItem.stack,supported=ActorObjectPlacement.Supported(p.HeldItem),
                definitionSupported=bench!=null,anchorMeaning="TileObjectData origin in tile coordinates; default vanilla Work Bench only; complete dry footprint and support inside observed bounds; native placement not guaranteed"};
            return SceneGeometry.LimitObjectPlacements(placements,placementCap,diagnostics);
        });
        response["sample"]=new{phase="one bound game-thread query",sameGameUpdate=tick==Main.GameUpdateCount,elapsedMilliseconds=Stopwatch.GetElapsedTime(started).TotalMilliseconds};
        return response;
    }
}
