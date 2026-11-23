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
    private sealed record MineCell(int X,int Y);
    private static readonly HashSet<int> Ores=new(){TileID.Copper,TileID.Tin,TileID.Iron,TileID.Lead,TileID.Silver,TileID.Tungsten,
        TileID.Gold,TileID.Platinum,TileID.Demonite,TileID.Crimtane,TileID.Meteorite,TileID.Hellstone,TileID.Cobalt,TileID.Palladium,
        TileID.Mythril,TileID.Orichalcum,TileID.Adamantite,TileID.Titanium,TileID.Chlorophyte};
    internal static object Read(Player p,AiReadOnlyProtocol.Identity identity,JsonElement args)
    {
        var layers=SceneLayers.Read(args);
        var scope=SceneScope.Read(args);int width=scope.Width,height=scope.Height;
        long started=Stopwatch.GetTimestamp();ulong tick=Main.GameUpdateCount;
        long allocated=scope.Region?GC.GetAllocatedBytesForCurrentThread():0;
        string sceneId=identity.World+":scene:"+(++sequence);
        int Cap(string key,int fallback)=>args.TryGetProperty(key,out var n)?n.GetInt32():fallback;
        int objectCap=Cap("maxObjects",16),landingCap=Cap("maxRegions",12),placementCap=Cap("maxPlacements",12),hazardCap=Cap("maxHazards",24);
        int ox=(int)Math.Floor(p.Center.X/16)-width/2,
            oy=scope.Region?(int)Math.Floor(p.Center.Y/16)-height/2:(int)Math.Floor(p.Bottom.Y/16)-17;
        var cells=new SceneCell[width,height];var anchors=new Dictionary<(int,int),object>();var content=new Dictionary<int,string>();
        string Key(int type){if(!content.TryGetValue(type,out var key))content[type]=key=TileLoader.GetTile(type)?.FullName??"Terraria/"+TileID.Search.GetName(type);return key;}
        int unknown=0,uninterpreted=0,unknownNavigation=0,sampled=0;
        bool samplingStopped=false;
        var unknownReasons=new Dictionary<string,int>();
        var notSampled=new SceneCell(new("not_sampled",CollisionKnowledge:"not_sampled"),'?');
        var sources=new List<string>();
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            if(scope.Region&&(x+y*width)%128==0&&Stopwatch.GetElapsedTime(started).TotalMilliseconds>=SceneScope.SamplingBudgetMs)
                samplingStopped=true;
            if(samplingStopped)
            {
                cells[x,y]=notSampled;unknown++;uninterpreted++;unknownNavigation++;
                unknownReasons["not_sampled"]=unknownReasons.GetValueOrDefault("not_sampled")+1;
                continue;
            }
            var c=ObservationReader.ReadCell(x+ox,y+oy,anchors);
            sampled++;
            if(c.Knowledge!="read")unknown++;
            if(c.Knowledge!="read")unknownReasons[c.Knowledge]=unknownReasons.GetValueOrDefault(c.Knowledge)+1;
            if(c.CollisionKnowledge!="known")uninterpreted++;
            SceneFurniture furniture=null;
            string kind=null;int? ax=null,ay=null;sources.Clear();
            if(c.LiquidAmount>0)sources.Add(c.LiquidType switch{0=>"water",1=>"lava",2=>"honey",3=>"shimmer",_=>"unknown_liquid"});
            if(c.HasTile&&!c.Actuated)
            {
                if(c.TouchDamage>0)sources.Add("touch_damage");if(c.Hot)sources.Add("touch_hot");
                if(c.Bleeding)sources.Add("touch_bleeding");if(c.DestroyOnTouch)sources.Add("touch_destroy");if(c.Boulder)sources.Add("boulder_tile");
                int type=c.TileType??-1;
                if(TerrainReader.UnsupportedMotionSurface(type))sources.Add("unsupported_motion_surface");
            }
            if((layers.Has("map")||layers.Has("objects"))&&c.HasTile&&c.TileType is int t)
            {
                if(t<TileID.Count&&TileID.Sets.IsATreeTrunk[t])kind="tree_component";
                else if(Ores.Contains(t))kind="ore_component";
                else if(t is TileID.Containers or TileID.Containers2 or TileID.Dressers)kind="chest";
                else if(t is TileID.OpenDoor or TileID.ClosedDoor)kind="door";
                else if(t==TileID.Chairs)kind="chair";
                else if(t==TileID.WorkBenches)kind="workbench";
                else if(t==TileID.Torches)kind="light_source";
                if(layers.Has("objects")&&kind is ("chest" or "door" or "workbench" or "chair"))
                {
                    var left=TileObjectData.TopLeft(x+ox,y+oy);
                    ax=left.X;ay=left.Y;
                    if(kind!="chest" && TileObjectData.GetTileData(Main.tile[x+ox,y+oy]) is {} shape)
                    {
                        furniture=new(kind,new(left.X,left.Y,shape.Width,shape.Height),new(shape.Origin.X,shape.Origin.Y),
                            new(left.X+shape.Origin.X,left.Y+shape.Origin.Y),
                            kind=="door"?(t==TileID.OpenDoor?"open":"closed"):null);
                    }
                }
                else if(layers.Has("objects")&&kind=="light_source")
                    furniture=new("torch",new(x+ox,y+oy,1,1),new(0,0),new(x+ox,y+oy));
            }
            char nav=TerrainReader.ReadCell(x+ox,y+oy);
            if(nav=='?')unknownNavigation++;
            if(sources.Contains("unsupported_motion_surface"))nav='!';
            bool placementFacts=layers.Has("objectPlacementCandidates")&&c.TileType is int placementType&&placementType>=0&&placementType<TileID.Count;
            cells[x,y]=new(c,nav,kind,(layers.Has("objects")||layers.Has("hazards"))&&c.TileType is int material&&(kind!=null||sources.Count>0)?Key(material):null,ax,ay,sources.ToArray(),
                placementFacts&&Main.tileCut[c.TileType.Value],placementFacts&&TileID.Sets.BreakableWhenPlacing[c.TileType.Value],furniture);
        }
        var actor=new WorldRect(p.position.X,p.position.Y,p.width,p.height);
        bool Intersects(Entity e)=>e.position.X<(ox+width)*16&&e.position.X+e.width>ox*16&&e.position.Y<(oy+height)*16&&e.position.Y+e.height>oy*16;
        var bodies=layers.Has("placementCandidates")||layers.Has("objectPlacementCandidates")?Main.player.Where(other=>other.active&&!other.dead&&!other.ghost&&Intersects(other)).Select(other=>new WorldRect(other.position.X,other.position.Y,other.width,other.height)).ToList():new List<WorldRect>();
        if(!bodies.Contains(actor))bodies.Add(actor);
        var objects=layers.Has("objects")?SceneGeometry.Objects(cells,ox,oy,scope.Region?null:(tx,ty)=>new MineCell(tx,ty),p.Center.X,p.Center.Y,
            scope.Region?SceneRegion.MaxObjectGroups:int.MaxValue).ToList():new List<SceneObject>();
        bool objectGroupingTruncated=scope.Region&&objects.Count>=SceneRegion.MaxObjectGroups;
        var dynamicHazards=new List<(double? Contact, float Distance, object Value)>();
        var actorVelocity = new StateVector(p.velocity.X, p.velocity.Y);
        void AddHazard(Entity entity, string kind, int typeId, int damage, string classification,
            string entityRef, string classificationBasis, int extraUpdates = 0)
        {
            var bounds = new WorldRect(entity.position.X, entity.position.Y, entity.width, entity.height);
            var velocity = new StateVector(entity.velocity.X, entity.velocity.Y);
            var threat = SceneThreat.Project(actor, actorVelocity, bounds, velocity, extraUpdates);
            dynamicHazards.Add((threat.ContactAfterTicks, HazardDistance(entity), new
            {
                kind, slot = entity.whoAmI, typeId, damage, classification, entityRef,
                observationRef = $"{sceneId}:{kind}:{entity.whoAmI}",
                observedGameUpdate = tick, boundsWorldPixels = bounds,
                velocity = float.IsFinite(velocity.X) && float.IsFinite(velocity.Y) ? velocity : null,
                extraUpdates,
                knowledge = classificationBasis,
                threat
            }));
        }
        float HazardDistance(Entity e)=>Math.Abs(e.Center.X-p.Center.X)+Math.Abs(e.Center.Y-p.Center.Y);int entitiesScanned=0;
        var entityMarkers=new List<(int,int,char)>();
        void EntityObject(Entity e,string kind,int? type,string key,object detail,char glyph)
        {
            int cx=(int)Math.Floor((e.position.X+e.width/2f)/16),cy=(int)Math.Floor((e.position.Y+e.height/2f)/16);
            if(layers.Has("objects"))objects.Add(new("e"+(++entitiesScanned),kind,type,key,new(cx,cy,1,1),1,false,
                new{boundsWorldPixels=new WorldRect(e.position.X,e.position.Y,e.width,e.height),velocity=new StateVector(e.velocity.X,e.velocity.Y),detail}));
            if(layers.Has("map"))entityMarkers.Add((Math.Clamp(cx-ox,0,width-1),Math.Clamp(cy-oy,0,height-1),glyph));
        }
        if(layers.Has("map")||layers.Has("objects")||layers.Has("hazards"))foreach(var n in Main.npc)if(n.active&&Intersects(n))
        {
            var evidence=new NpcEvidence(n.friendly,n.townNPC||NPCID.Sets.ActsLikeTownNPC[n.type],NPCID.Sets.CountsAsCritter[n.type],n.damage,n.CanBeChasedBy(),n.ModNPC!=null,NPCID.Sets.ProjectileNPC[n.type]);
            var classification=EntityCollector.Classify(evidence);
            string entityRef=identity.World+":"+identity.Generation+":npc:"+n.whoAmI+":"+n.GetGlobalNPC<AtomicNpcIdentity>().Token;
            EntityObject(n,"npc",n.type,n.ModNPC?.FullName??"Terraria/"+NPCID.Search.GetName(n.type),new{slot=n.whoAmI,entityRef,n.life,n.damage,classification=classification.Kind,basis=classification.Basis},classification.Kind=="hostile_creature"?'!':'N');
            if(layers.Has("hazards")&&classification.Kind is ("hostile_creature" or "unknown"))
                AddHazard(n, "npc", n.type, n.damage, classification.Kind, entityRef, classification.Basis);
        }
        if(layers.Has("map")||layers.Has("objects"))for(int i=0;i<Main.item.Length;i++)
        {var item=Main.item[i];if(item.active&&!item.IsAir&&Intersects(item))EntityObject(item,"drop",item.type,item.ModItem?.FullName??"Terraria/"+ItemID.Search.GetName(item.type),new{slot=i,item.stack,pickup="not evaluated"},'$');}
        if(layers.Has("map")||layers.Has("objects"))foreach(var other in Main.player)if(other.active&&!ReferenceEquals(other,p)&&Intersects(other))EntityObject(other,"player",null,"Terraria/Player",new{slot=other.whoAmI,other.dead,other.ghost},'P');
        if(layers.Has("map")||layers.Has("hazards"))foreach(var projectile in Main.projectile)if(projectile.active&&projectile.hostile&&Intersects(projectile))
        {
            if(layers.Has("hazards"))
                AddHazard(projectile, "hostile_projectile", projectile.type, projectile.damage, "hostile_projectile",
                    null, "native hostile flag", projectile.extraUpdates);
            if(layers.Has("map"))entityMarkers.Add((Math.Clamp((int)(projectile.Center.X/16)-ox,0,width-1),Math.Clamp((int)(projectile.Center.Y/16)-oy,0,height-1),'*'));
        }
        int actorX=Math.Clamp((int)(p.Center.X/16)-ox,0,width-1),actorY=Math.Clamp((int)(p.Center.Y/16)-oy,0,height-1);
        var geometry=new Lazy<string[]>(()=>SceneGeometry.GeometryRows(cells));
        var regionalGeometry=new Lazy<object>(()=>SceneRegion.Encode(cells,c=>SceneGeometry.Geometry(c).ToString()));
        var response=new Dictionary<string,object>{
            ["schemaVersion"]=scope.Region?3:2,["sceneId"]=sceneId,["gameUpdate"]=tick,
            ["lighting"]=ServerAiSystem.Lighting?.Observe(p)??new{available=false,source="dedicated_server",reason="tile_light_map_unavailable"},
            ["bounds"]=new{minX=ox,minY=oy,maxX=ox+width-1,maxY=oy+height-1},["tileSize"]=16,
            ["actor"]=new{slot=p.whoAmI,boundsWorldPixels=actor,feetY=p.Bottom.Y,velocity=new StateVector(p.velocity.X,p.velocity.Y),p.dead,
                navigationProfileSupported=GameNavigation.Ordinary(p),mapTile=new{x=ox+actorX,y=oy+actorY},
                bodyTileBounds=BodyTileBounds.FromPixels(p.position.X,p.position.Y,p.width,p.height)},
            ["coverage"]=new{scannedTiles=1305,unknownCells=unknown,uninterpretedCollisionCells=uninterpreted,unknownNavigationCells=unknownNavigation,geometryComplete=unknown==0&&uninterpreted==0&&unknownNavigation==0},
            ["limits"]="observation-scoped IDs; no visibility, contents, persistent map or custom Mod hook interpretation; truncation is per collection, never empty=absent",
            ["includedLayers"]=layers.Included};
        if(scope.Region)
        {
            response["scale"]="region";
            response["coverage"]=new{requestedTiles=width*height,scannedTiles=sampled,unknownCells=unknown,
                unknownByReason=unknownReasons,uninterpretedCollisionCells=uninterpreted,unknownNavigationCells=unknownNavigation,
                geometryComplete=unknown==0&&uninterpreted==0&&unknownNavigation==0,samplingTruncated=samplingStopped,
                knowledge=SceneRegion.Encode(cells,c=>new SceneKnowledge(c.Facts.Knowledge,c.Facts.CollisionKnowledge,c.Navigation!='?'))};
            response["limits"]="current server Tile facts, not camera visibility; region has no action targets or reachability proof; L0/native collision must approve execution; query_map is separate last-known history";
            layers.Add(response,"map",()=>new{originX=ox,originY=oy,terrain=regionalGeometry.Value,
                actor=new{x=ox+actorX,y=oy+actorY},markers=SceneGeometry.Limit(entityMarkers.Select(m=>new{x=ox+m.Item1,y=oy+m.Item2,glyph=m.Item3.ToString()}),128)});
            layers.Add(response,"geometry",()=>new{originX=ox,originY=oy,grid=regionalGeometry.Value});
        }
        else
        {
            layers.Add(response,"map",()=>new{rows=SceneGeometry.MapRows(cells,geometry.Value,actorX,actorY,entityMarkers),originX=ox,originY=oy});
            layers.Add(response,"geometry",()=>new{rows=geometry.Value,originX=ox,originY=oy});
        }
        layers.Add(response,"objects",()=>
        {
            float Distance(SceneObject o)=>Math.Abs((o.ObservedBounds.X+o.ObservedBounds.Width/2f)*16-p.Center.X)+Math.Abs((o.ObservedBounds.Y+o.ObservedBounds.Height/2f)*16-p.Center.Y);
            var result=SceneGeometry.LimitObjects(objects,objectCap,Distance);
            result=result with{Items=result.Items.Select(o=>o.Target is MineCell tile?o with{Target=SceneMineTargets.Observe(identity,tile.X,tile.Y)}:o).ToArray()};
            response["objectCounts"]=new{observed=objects.GroupBy(o=>o.Kind).ToDictionary(g=>g.Key,g=>g.Count()),returned=result.Items.GroupBy(o=>o.Kind).ToDictionary(g=>g.Key,g=>g.Count())};
            if(scope.Region)
            {
                response["objectCounts"]=new{observed=objects.GroupBy(o=>o.Kind).ToDictionary(g=>g.Key,g=>g.Count()),
                    returned=result.Items.GroupBy(o=>o.Kind).ToDictionary(g=>g.Key,g=>g.Count()),observedCountsComplete=!objectGroupingTruncated&&!samplingStopped&&unknown==0};
                return new{items=result.Items,matchedCount=objectGroupingTruncated?(int?)null:result.MatchedCount,
                    returnedCount=result.ReturnedCount,truncated=objectGroupingTruncated||result.Truncated,
                    omittedCount=objectGroupingTruncated?(int?)null:result.OmittedCount,matchedCountLowerBound=result.MatchedCount,
                    groupingTruncated=objectGroupingTruncated,maxGroups=SceneRegion.MaxObjectGroups,
                    kindGrid=SceneRegion.Encode(cells,c=>new SceneRegionObjectCell(c.Kind??"",c.ContentKey))};
            }
            return result;
        });
        layers.Add(response,"hazards",()=>
        {
            var tileHazards=SceneGeometry.Hazards(cells,ox,oy).OrderBy(h=>Math.Max(0,Math.Max(h.MinX*16-p.Center.X,p.Center.X-(h.MaxX+1)*16))+Math.Abs(h.Y*16+8-p.Center.Y)).ThenBy(h=>h.Y).ThenBy(h=>h.MinX);
            var result=new Dictionary<string,object>{["tiles"]=SceneGeometry.Limit(tileHazards,hazardCap),
                ["entities"]=SceneGeometry.Limit(dynamicHazards.OrderBy(h=>h.Contact ?? double.PositiveInfinity)
                    .ThenBy(h=>h.Distance).Select(h=>h.Value),hazardCap)};
            if(scope.Region)
            {
                result["tileMask"]=SceneRegion.Encode(cells,SceneRegion.HazardMask);
                result["bitSources"]=SceneRegion.HazardSources;
            }
            return result;
        });
        layers.Add(response,"landingRegions",()=>
        {
            var world=new NativeMotionWorld(new(p.position.X,p.position.Y,p.velocity.X,p.velocity.Y,TerrainReader.Read(p).Grounded));
            var landings=p.width==20&&p.height==42?SceneGeometry.Landings(cells,ox,oy,p.position.X,p.Bottom.Y,world.SafeFlatLanding):Array.Empty<SceneLanding>();
            return SceneGeometry.LimitLandings(landings,landingCap);
        });
        layers.Add(response,"placementCandidates",()=>
        {
            var item=p.HeldItem;
            var material=new Dictionary<string,object>{
                ["itemType"]=item.type,["stack"]=item.stack,["createTile"]=item.createTile,
                ["hotbarKey"]=HotbarSlots.KeyForInventorySlot(p.selectedItem),["supported"]=ActorPlacement.Supported(item)};
            if(ActorPlacement.UnifiedAvailable)
            {
                string kind=ActorPlacement.UnifiedKind(item);
                var shape=kind is "object" or "chest"?ActorObjectPlacement.Definition(item):null;
                material["placeItem"]=new{kind,createWall=item.createWall,style=item.placeStyle,
                    width=shape?.Width??1,height=shape?.Height??1,originX=shape?.OriginX??0,originY=shape?.OriginY??0};
            }
            response["placementMaterial"]=material;
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
        if(scope.Region)
        {
            int bytes=JsonSerializer.SerializeToUtf8Bytes(response,SceneRegion.Json).Length;
            response["sample"]=new{phase="one bound game-thread query",source="server_world_tile_current",sameGameUpdate=tick==Main.GameUpdateCount,
                elapsedMilliseconds=Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                allocatedBytes=GC.GetAllocatedBytesForCurrentThread()-allocated,payloadBytesBeforeMetrics=bytes,
                samplingBudgetMilliseconds=SceneScope.SamplingBudgetMs,maxTiles=SceneScope.MaxTiles,
                samplingBudgetReached=samplingStopped};
        }
        return response;
    }
}
