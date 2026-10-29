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
            if(c.HasTile&&c.TileType is int t)
            {
                if(t<TileID.Count&&TileID.Sets.IsATreeTrunk[t])kind="tree_component";
                else if(Ores.Contains(t))kind="ore_component";
                else if(t is TileID.Containers or TileID.Containers2 or TileID.Dressers)kind="chest";
                else if(t is TileID.OpenDoor or TileID.ClosedDoor)kind="door";
                else if(t==TileID.WorkBenches)kind="workbench";
                if(kind is "chest" or "door" or "workbench")
                {var a=TileObjectData.TopLeft(x+ox,y+oy);ax=a.X;ay=a.Y;}
            }
            char nav=TerrainReader.ReadCell(x+ox,y+oy);
            if(nav=='?')unknownNavigation++;
            if(sources.Contains("unsupported_motion_surface"))nav='!';
            cells[x,y]=new(c,nav,kind,c.TileType is int material&&(kind!=null||sources.Count>0)?Key(material):null,ax,ay,sources.ToArray());
        }
        var actor=new WorldRect(p.position.X,p.position.Y,p.width,p.height);
        bool Intersects(Entity e)=>e.position.X<(ox+45)*16&&e.position.X+e.width>ox*16&&e.position.Y<(oy+29)*16&&e.position.Y+e.height>oy*16;
        var bodies=Main.player.Where(other=>other.active&&!other.dead&&!other.ghost&&Intersects(other)).Select(other=>new WorldRect(other.position.X,other.position.Y,other.width,other.height)).ToList();
        if(!bodies.Contains(actor))bodies.Add(actor);
        var objects=SceneGeometry.Objects(cells,ox,oy).ToList();
        var dynamicHazards=new List<(float Distance,object Value)>();
        float HazardDistance(Entity e)=>Math.Abs(e.Center.X-p.Center.X)+Math.Abs(e.Center.Y-p.Center.Y);int entitiesScanned=0;
        var entityMarkers=new List<(int,int,char)>();
        void EntityObject(Entity e,string kind,int? type,string key,object detail,char glyph)
        {
            int cx=(int)Math.Floor((e.position.X+e.width/2f)/16),cy=(int)Math.Floor((e.position.Y+e.height/2f)/16);
            objects.Add(new("e"+(++entitiesScanned),kind,type,key,new(cx,cy,1,1),1,false,"current entity bounds intersect local map; slot is not persistent identity",
                new{boundsWorldPixels=new WorldRect(e.position.X,e.position.Y,e.width,e.height),velocity=new StateVector(e.velocity.X,e.velocity.Y),detail}));
            entityMarkers.Add((Math.Clamp(cx-ox,0,44),Math.Clamp(cy-oy,0,28),glyph));
        }
        foreach(var n in Main.npc)if(n.active&&Intersects(n))
        {
            var evidence=new NpcEvidence(n.friendly,n.townNPC||NPCID.Sets.ActsLikeTownNPC[n.type],NPCID.Sets.CountsAsCritter[n.type],n.damage,n.CanBeChasedBy(),n.ModNPC!=null,NPCID.Sets.ProjectileNPC[n.type]);
            var classification=EntityCollector.Classify(evidence);
            string entityRef=identity.World+":"+identity.Generation+":npc:"+n.whoAmI+":"+n.GetGlobalNPC<AtomicNpcIdentity>().Token;
            EntityObject(n,"npc",n.type,n.ModNPC?.FullName??"Terraria/"+NPCID.Search.GetName(n.type),new{slot=n.whoAmI,entityRef,n.life,n.damage,classification=classification.Kind,basis=classification.Basis},classification.Kind=="hostile_creature"?'!':'N');
            if(classification.Kind is "hostile_creature" or "unknown")dynamicHazards.Add((HazardDistance(n),new{kind="npc",slot=n.whoAmI,entityRef,classification=classification.Kind,boundsWorldPixels=new WorldRect(n.position.X,n.position.Y,n.width,n.height),n.damage,knowledge="inferred classification; no current threat or path prediction"}));
        }
        for(int i=0;i<Main.item.Length;i++)
        {var item=Main.item[i];if(item.active&&!item.IsAir&&Intersects(item))EntityObject(item,"drop",item.type,item.ModItem?.FullName??"Terraria/"+ItemID.Search.GetName(item.type),new{slot=i,item.stack,pickup="not evaluated"},'$');}
        foreach(var other in Main.player)if(other.active&&!ReferenceEquals(other,p)&&Intersects(other))EntityObject(other,"player",null,"Terraria/Player",new{slot=other.whoAmI,other.dead,other.ghost},'P');
        foreach(var projectile in Main.projectile)if(projectile.active&&projectile.hostile&&Intersects(projectile))
        {
            dynamicHazards.Add((HazardDistance(projectile),new{kind="hostile_projectile",slot=projectile.whoAmI,typeId=projectile.type,projectile.damage,
                boundsWorldPixels=new WorldRect(projectile.position.X,projectile.position.Y,projectile.width,projectile.height),velocity=new StateVector(projectile.velocity.X,projectile.velocity.Y),knowledge="current hostile flag; no hit/trajectory prediction"}));
            entityMarkers.Add((Math.Clamp((int)(projectile.Center.X/16)-ox,0,44),Math.Clamp((int)(projectile.Center.Y/16)-oy,0,28),'*'));
        }
        var geometry=new string[29];var map=new char[29][];
        for(int y=0;y<29;y++)
        {
            geometry[y]=new(Enumerable.Range(0,45).Select(x=>SceneGeometry.Geometry(cells[x,y])).ToArray());
            map[y]=Enumerable.Range(0,45).Select(x=>cells[x,y].Kind==null?geometry[y][x]:SceneGeometry.Glyph(cells[x,y].Kind)).ToArray();
        }
        foreach(var marker in entityMarkers)map[marker.Item2][marker.Item1]=marker.Item3;
        int actorX=Math.Clamp((int)(p.Center.X/16)-ox,0,44),actorY=Math.Clamp((int)(p.Center.Y/16)-oy,0,28);
        map[actorY][actorX]='@';
        for(int y=0;y<29;y++)for(int x=0;x<45;x++)
        {
            char danger=SceneGeometry.Danger(cells[x,y].Sources);
            if(danger!='\0')map[y][x]=danger;
            if(geometry[y][x]=='?')map[y][x]='?';
        }
        var world=new NativeMotionWorld(new(p.position.X,p.position.Y,p.velocity.X,p.velocity.Y,TerrainReader.Read(p).Grounded));
        var landings=p.width==20&&p.height==42?SceneGeometry.Landings(cells,ox,oy,p.position.X,p.Bottom.Y,world.SafeFlatLanding):Array.Empty<SceneLanding>();
        var placements=SceneGeometry.Placements(cells,ox,oy,actor,bodies);
        var bench=ActorObjectPlacement.Definition();
        var objectPlacements=SceneGeometry.ObjectPlacements(cells,ox,oy,actor,bodies,
            bench?.Width??0,bench?.Height??0,bench?.OriginX??0,bench?.OriginY??0,bench?.SupportStart??0,bench?.SupportCount??0,out var placementDiagnostics);
        float Distance(SceneObject o)=>Math.Abs((o.ObservedBounds.X+o.ObservedBounds.Width/2f)*16-p.Center.X)+Math.Abs((o.ObservedBounds.Y+o.ObservedBounds.Height/2f)*16-p.Center.Y);
        var objectResult=SceneGeometry.Limit(objects.OrderBy(Distance).ThenBy(o=>o.Id,StringComparer.Ordinal),objectCap);
        var tileHazards=SceneGeometry.Hazards(cells,ox,oy).OrderBy(h=>Math.Max(0,Math.Max(h.MinX*16-p.Center.X,p.Center.X-(h.MaxX+1)*16))+Math.Abs(h.Y*16+8-p.Center.Y)).ThenBy(h=>h.Y).ThenBy(h=>h.MinX);
        return new{schemaVersion=1,sceneId,gameUpdate=tick,bounds=new{minX=ox,minY=oy,maxX=ox+44,maxY=oy+28},tileSize=16,
            actor=new{slot=p.whoAmI,boundsWorldPixels=actor,feetY=p.Bottom.Y,velocity=new StateVector(p.velocity.X,p.velocity.Y),p.dead,
                navigationProfileSupported=GameNavigation.Ordinary(p),mapTile=new{x=ox+actorX,y=oy+actorY}},
            map=new{rows=map.Select(r=>new string(r)).ToArray(),originX=ox,originY=oy,
                legend="side view: .=nonblocking #=solid h=halfblock 1..4=slopes ==flat platform a/b=sloped platform; T=trunk component O=ore C=chest D=door W=workbench $=drop N=NPC P=player *=hostile projectile @=AI; ~=water L=lava H=honey S=shimmer !=touch/NPC hazard B=boulder M=unsupported motion surface ?=unknown",
                priority="unknown collision > tile hazards > actor/entity/object > geometry; overlapping facts remain in geometry/objects/hazards; edge entity markers may be clamped"},
            geometry=new{rows=geometry,originX=ox,originY=oy,legend="same shapes as navigation, liquids/hazards separated; A=actuated nonblocking; a/b=sloped platforms",meaning="nonblocking is not walkable; walls behind tiles do not block"},
            objects=objectResult,objectCounts=new{observed=objects.GroupBy(o=>o.Kind).ToDictionary(g=>g.Key,g=>g.Count()),returned=objectResult.Items.GroupBy(o=>o.Kind).ToDictionary(g=>g.Key,g=>g.Count())},
            hazards=new{tiles=SceneGeometry.Limit(tileHazards,hazardCap),entities=SceneGeometry.Limit(dynamicHazards.OrderBy(h=>h.Distance).Select(h=>h.Value),hazardCap),
                meaning="liquid facts, contact damage flags and movement exclusions are distinct; glyph is highest-priority source, structured sources may overlap"},
            landingRegions=SceneGeometry.Limit(landings,landingCap),placementCandidates=SceneGeometry.Limit(placements,placementCap),
            objectPlacementCandidates=SceneGeometry.LimitObjectPlacements(objectPlacements,placementCap,placementDiagnostics),
            objectPlacementMaterial=new{itemType=p.HeldItem.type,p.HeldItem.stack,supported=ActorObjectPlacement.Supported(p.HeldItem),
                definitionSupported=bench!=null,anchorMeaning="TileObjectData origin in tile coordinates; default vanilla Work Bench only; complete dry footprint and support inside observed bounds; native placement not guaranteed"},
            placementMaterial=new{itemType=p.HeldItem.type,p.HeldItem.stack,p.HeldItem.createTile,hotbarKey=HotbarSlots.KeyForInventorySlot(p.selectedItem),supported=ActorPlacement.Supported(p.HeldItem)},
            coverage=new{scannedTiles=1305,unknownCells=unknown,uninterpretedCollisionCells=uninterpreted,unknownNavigationCells=unknownNavigation,geometryComplete=unknown==0&&uninterpreted==0&&unknownNavigation==0,
                objectScope="vanilla trunks, 19 ore types, chests/dressers, doors/workbenches and intersecting active entities; other objects not classified",
                treeIdentity="connected observed cells may split one tree or merge adjacent trees; NOT biological tree count; tree complete is always false",
                candidateScope="geometric only; no route, native placement permission or global completeness guarantee; placement rule is conservative solid cardinal attachment, dry empty cell, player exclusion and 80px"},
            sample=new{phase="one bound game-thread query",sameGameUpdate=tick==Main.GameUpdateCount,elapsedMilliseconds=Stopwatch.GetElapsedTime(started).TotalMilliseconds},
            limits="observation-scoped IDs; no visibility/lighting, contents, persistent map or custom Mod hook interpretation; truncation is per collection, never empty=absent"};
    }
}
