using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Explicit diagnostic scenario injection. Never enabled by normal Host & Play.
internal static class ServerCraftingFixture
{
    internal static void Tick(Player p,int left,int floor)
    {
        if(!Program.LaunchParameters.ContainsKey("-companioncrafttest"))return;
        string request=Path.Combine(Main.SavePath,"craft-request.json");if(!File.Exists(request))return;
        if(ActorControl.Running||ActorItemUse.Running||ActorMining.Running)throw new InvalidOperationException("Craft fixture cannot replace an active actor");
        var spec=JsonDocument.Parse(File.ReadAllText(request));string kind=spec.RootElement.GetProperty("caseId").GetString();File.Delete(request);
        for(int x=left-8;x<=left+6;x++)for(int y=floor-38;y<=floor+2;y++)
        {var t=Main.tile[x,y];t.ClearEverything();if(y>=floor){t.HasTile=true;t.TileType=TileID.Grass;}}
        for(int i=0;i<Main.item.Length;i++)if(Main.item[i].active)Main.item[i].active=false;
        for(int i=0;i<58;i++)p.inventory[i]=new Item();
        p.position=new(left*16,floor*16-p.height);p.velocity=Vector2.Zero;p.selectedItem=0;
        p.chest=-1;
        void Give(int slot,int type,int count){p.inventory[slot].SetDefaults(type);p.inventory[slot].stack=count;}
        int resultType=0;
        Give(0,ItemID.Wood,kind=="insufficient"?9:50);
        if(kind is "station_present" or "station_absent")
        {
            if(kind=="station_present"&&(!TileObject.CanPlace(left+2,floor-1,TileID.WorkBenches,0,1,out var data)||!TileObject.Place(data)))throw new InvalidOperationException("Native workbench fixture placement failed");
        }
        else if(kind=="harvest")
        {
            p.inventory[0]=new Item();Give(1,ItemID.CopperAxe,1);
            if(!WorldGen.GrowTree(left-3,floor))throw new InvalidOperationException("Native crafting harvest tree failed");
        }
        else if(kind=="group"){Give(0,ItemID.BorealWood,30);Give(2,ItemID.Gel,3);}
        else if(kind is "full" or "partial_space")
        {
            for(int i=0;i<50;i++)Give(i,ItemID.StoneBlock,9999);
            Give(0,ItemID.Wood,10);
            if(kind=="partial_space")Give(1,ItemID.WoodPlatform,9999-Main.recipe.First(r=>r.createItem.type==ItemID.WoodPlatform&&r.requiredItem.Any(i=>i.type==ItemID.Wood)).createItem.stack);
        }
        else if(kind is "water_present" or "water_absent")
        {
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic;
            var waterRecipe=Main.recipe.First(r=>!r.Disabled&&r.Mod==null&&r.Conditions.Contains(Condition.NearWater)&&r.requiredTile.Count==0&&r.requiredItem.Count>0
                &&typeof(Recipe).GetProperty("ConsumeIngredientHooks",flags).GetValue(r)==null&&typeof(Recipe).GetProperty("OnCraftHooks",flags).GetValue(r)==null);
            resultType=waterRecipe.createItem.type;
            for(int i=0;i<58;i++)p.inventory[i]=new Item();
            for(int i=0;i<waterRecipe.requiredItem.Count;i++)Give(i,waterRecipe.requiredItem[i].type,waterRecipe.requiredItem[i].stack*3);
            if(kind=="water_present")
            {
                int px=left-2,py=floor-1;
                for(int dx=-1;dx<=1;dx++)for(int dy=-1;dy<=1;dy++){var t=Main.tile[px+dx,py+dy];t.HasTile=true;t.TileType=TileID.Stone;}
                var pool=Main.tile[px,py];pool.ClearEverything();pool.LiquidType=0;pool.LiquidAmount=255;
            }
        }
        else if(kind is "cancel" or "selftest")Give(0,ItemID.Wood,1000);
        else if(kind!="insufficient")throw new InvalidOperationException("Unknown craft fixture case");
        NetworkStateSync.Full(p);
        if(kind=="selftest")SelfTest(p);
        File.WriteAllText(Path.Combine(Main.SavePath,"craft-ready.json"),JsonSerializer.Serialize(new{caseId=kind,resultType,world=ProbeSession.WorldSession,generation=ProbeSession.ActorGeneration,slot=p.whoAmI,tree=new{x=left-3,y=floor-1},mode="isolated fixture; native tree/bench, diagnostic inventory/pose only"}));
    }
    private static void SelfTest(Player p)
    {
        var id=new AiReadOnlyProtocol.Identity(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration,p.whoAmI,null);
        var human=Main.player[0];var humanBag=human.inventory.Select(i=>(i.type,i.stack)).ToArray();
        int myPlayer=Main.myPlayer,focus=Main.focusRecipe;bool inventory=Main.playerInventory;
        var mouse=Main.mouseItem;var recipes=Main.availableRecipe.ToArray();var driver=ActorControl.Driver;
        var ownedField=typeof(Recipe).GetField("_ownedItems",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
        var owned=ownedField.GetValue(null);
        try
        {
            Main.myPlayer=0;Main.mouseItem=new Item(ItemID.Diamond,17);Main.playerInventory=true;
            var sentinel=Main.mouseItem;
            ActorCrafting.ReadRecipes(id,JsonSerializer.SerializeToElement(new{resultType=ItemID.WorkBench}));
            int index=Array.FindIndex(Main.recipe,r=>r.createItem.type==ItemID.WorkBench&&r.requiredItem.Any(i=>i.type==ItemID.Wood));
            var executor=new ServerAiNativeActions();
            var handle=executor.Start(id,"craft_recipe",JsonSerializer.SerializeToElement(new{recipeId=index,recipeToken=ActorCrafting.Token(id,index,Main.recipe[index]),crafts=20}),"diagnostic-craft-cancel");
            var first=executor.Poll(id,handle);var stopped=executor.Cancel(id,handle);
            var evidence=JsonSerializer.SerializeToElement(stopped.Evidence);
            if(first.Status!="running"||stopped.Status!="cancelled"||!stopped.InputsReleased||evidence.GetProperty("completedCrafts").GetInt32()!=1)
                throw new InvalidOperationException("Partial native craft cancellation executed an extra batch");
            p.itemAnimation=1;
            object refused;
            try{refused=executor.Start(id,"craft_recipe",JsonSerializer.SerializeToElement(new{recipeId=index,recipeToken=ActorCrafting.Token(id,index,Main.recipe[index]),crafts=1}),"diagnostic-busy-cancel");}
            finally{p.itemAnimation=0;}
            var busyCancel=executor.Cancel(id,refused);
            if(busyCancel.Status!="failed"||busyCancel.Reason!="actor_busy"||!busyCancel.InputsReleased)throw new InvalidOperationException("Rejected craft cancellation lost known no-mutation evidence");
            if(Main.myPlayer!=0||!ReferenceEquals(Main.mouseItem,sentinel)||Main.mouseItem.stack!=17||Main.focusRecipe!=focus||!Main.playerInventory||!recipes.SequenceEqual(Main.availableRecipe)||!ReferenceEquals(owned,ownedField.GetValue(null))||!humanBag.SequenceEqual(human.inventory.Select(i=>(i.type,i.stack))))
                throw new InvalidOperationException("Crafting changed human/UI/cache state");
            File.WriteAllText(Path.Combine(Main.SavePath,"craft-selftest.json"),JsonSerializer.Serialize(new{humanAndUiUnchanged=true,ownedCacheRestored=true,busyCancelRejectedWithoutMutation=true,partialCancel=stopped.Evidence,cancelled=stopped.Status}));
        }
        finally{Main.myPlayer=myPlayer;Main.mouseItem=mouse;Main.playerInventory=inventory;ActorControl.Driver=driver;}
    }
}
