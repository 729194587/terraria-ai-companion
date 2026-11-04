using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MonoMod.Utils;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using CompanionBridge.ServerIpc;

namespace VirtualPlayerEngineProbe;

// Native crafting mechanics, isolated from local-player UI globals. One craft per Pump.
internal static class ActorCrafting
{
    internal sealed class Context
    {
        internal AiReadOnlyProtocol.Identity Identity;
        internal int RecipeId,Requested,Completed,Produced,OutputType,OutputCount;
        internal string Token,Status="running",Reason="awaiting_native_craft";
        internal bool MutationAttempted;
        internal Item Pending;
        internal readonly List<object> Batches=new();
    }
    [ThreadStatic] private static Player scope;
    [ThreadStatic] private static Item buffer;
    [ThreadStatic] private static Context currentStep;
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static Action<Recipe> consumeNative,craftNative;
    private static Action<Player> adjacencyNative,collectNative;
    private static Func<Player,Recipe,bool> tileRequirements,environmentRequirements;
    private static readonly Func<Item,Item,bool> sameItem=(Func<Item,Item,bool>)typeof(Item).GetMethod("IsTheSameAs",Flags).CreateDelegate(typeof(Func<Item,Item,bool>));
    private static readonly Dictionary<Condition,Func<bool>> conditions=new();
    private static readonly FieldInfo owned=typeof(Recipe).GetField("_ownedItems",Flags),consumed=typeof(RecipeLoader).GetField("ConsumedItems",Flags);
    private static readonly PropertyInfo consumeHooks=typeof(Recipe).GetProperty("ConsumeIngredientHooks",Flags),craftHooks=typeof(Recipe).GetProperty("OnCraftHooks",Flags);
    internal static Player Actor()=>scope!=null&&Program.IsMainThread&&ReferenceEquals(scope,ProbeSession.Actor)?scope:throw new InvalidOperationException("Crafting scope lost actor");
    private static List<Item> Consumed=>(List<Item>)consumed.GetValue(null);
    internal static void Consume(Recipe r)
    {
        Actor();consumeNative(r);
        currentStep.Batches.Add(new{consumed=Consumed.Select(i=>new{typeId=i.type,count=i.stack}).ToArray()});
    }
    internal static void Initialize()
    {
        // Called only after the existing exact engine fingerprint / isolation checks.
        T Copy<T>(MethodInfo method,string kind) where T:Delegate
        {
            using var d=new DynamicMethodDefinition(method);
            CraftingPatches.Patch(d.Definition,kind,d.Definition.Module.ImportReference(typeof(ActorCrafting).GetMethod(nameof(Actor),Flags)),
                d.Definition.Module.ImportReference(typeof(ActorCrafting).GetField("buffer",Flags)),
                d.Definition.Module.ImportReference(typeof(ActorCrafting).GetMethod(nameof(Consume),Flags)));
            return (T)d.Generate().CreateDelegate(typeof(T));
        }
        consumeNative=Copy<Action<Recipe>>(typeof(Recipe).GetMethod("Create",Flags,null,Type.EmptyTypes,null),"consume");
        craftNative=Copy<Action<Recipe>>(typeof(Main).GetMethod("CraftItem",Flags),"craft");
        adjacencyNative=Copy<Action<Player>>(typeof(Player).GetMethod("AdjTiles",Flags),"adjacency");
        collectNative=Copy<Action<Player>>(typeof(Recipe).GetMethod("CollectItemsToCraftWithFrom",Flags),"collect");
        tileRequirements=(Func<Player,Recipe,bool>)typeof(Recipe).GetMethod("PlayerMeetsTileRequirements",Flags).CreateDelegate(typeof(Func<Player,Recipe,bool>));
        environmentRequirements=(Func<Player,Recipe,bool>)typeof(Recipe).GetMethod("PlayerMeetsEnvironmentConditions",Flags).CreateDelegate(typeof(Func<Player,Recipe,bool>));
        conditions.Clear();
        foreach(string name in new[]{"NearWater","NearLava","NearHoney","NearShimmer","TimeDay","TimeNight"})
        {
            var c=(Condition)typeof(Condition).GetField(name,Flags).GetValue(null);
            var predicate=c.Predicate;
            using var d=new DynamicMethodDefinition(predicate.Method);
            CraftingPatches.Patch(d.Definition,"condition",d.Definition.Module.ImportReference(typeof(ActorCrafting).GetMethod(nameof(Actor),Flags)),null,null);
            conditions[c]=(Func<bool>)d.Generate().CreateDelegate(typeof(Func<bool>),predicate.Target);
        }
    }
    private static T InScope<T>(AiReadOnlyProtocol.Identity identity,Func<Player,T> action)
    {
        ServerAiNativeActions.Require(identity);
        if(scope!=null)throw new InvalidOperationException("Nested crafting scope");
        var p=(Player)identity.Actor;var previousOwned=owned.GetValue(null);var previousConsumed=Consumed.ToArray();
        scope=p;
        try
        {
            owned.SetValue(null,new Dictionary<int,int>());
            adjacencyNative(p);collectNative(p);
            var result=action(p);ServerAiNativeActions.Require(identity);return result;
        }
        finally{owned.SetValue(null,previousOwned);Consumed.Clear();Consumed.AddRange(previousConsumed);scope=null;buffer=null;currentStep=null;}
    }
    private static string Support(Recipe r)
    {
        if(r.Mod!=null||r.createItem.ModItem!=null||r.requiredItem.Any(i=>i.ModItem!=null))return "mod_recipe_or_item_unsupported";
        if(consumeHooks.GetValue(r)!=null||craftHooks.GetValue(r)!=null)return "special_recipe_callbacks_unsupported";
        if(r.Conditions.Any(c=>!conditions.ContainsKey(c)))return "special_environment_condition_unsupported";
        if(r.createItem.IsAir||r.createItem.stack<=0||r.createItem.stack>r.createItem.maxStack||r.requiredItem.Count==0)return "unsupported_recipe_shape";
        return null;
    }
    private static string Storage(Player p)=>p.chest!=-1||p.useVoidBag()?"external_crafting_storage_unsupported":null;
    internal static string Token(AiReadOnlyProtocol.Identity id,int index,Recipe r)
    {
        string definition=id.World+":"+id.Generation+":"+index+":"+RuntimeHelpers.GetHashCode(r)+":"+r.Disabled+":"+r.createItem.type+":"+r.createItem.stack+":"+
            string.Join(",",r.requiredItem.Select(i=>i.type+"="+i.stack))+":"+string.Join(",",r.requiredTile)+":"+string.Join(",",r.acceptedGroups)+":"+string.Join(",",r.Conditions.Select(c=>RuntimeHelpers.GetHashCode(c)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definition))).ToLowerInvariant();
    }
    private static bool Materials(Recipe r,Player p)
    {
        if(!Recipe.CollectedEnoughItemsToCraftRecipeNew(r))return false;
        // Native availability groups can over-count overlapping requirements. A dry,
        // sequential allocation mirrors ConsumeForCraft ordering without mutating Items.
        var left=p.inventory.Take(58).Select(i=>i.stack).ToArray();
        foreach(var ingredient in r.requiredItem)
        {
            int need=ingredient.stack;
            for(int i=0;i<58&&need>0;i++)if(sameItem(p.inventory[i],ingredient)||r.AcceptedByItemGroups(p.inventory[i].type,ingredient.type))
            {int n=Math.Min(left[i],need);left[i]-=n;need-=n;}
            if(need>0)return false;
        }
        return true;
    }
    private static bool Fits(Player p,Item output)
    {
        int space=0;
        foreach(var i in p.inventory.Take(50))
        {
            if(i.IsAir)space+=output.maxStack;
            else if(output.maxStack>1&&i.type==output.type&&i.prefix==0&&ItemLoader.CanStack(i,output))space+=Math.Max(0,i.maxStack-i.stack);
            if(space>=output.stack)return true;
        }
        return false;
    }
    private static string Available(Recipe r,Player p)
    {
        string unsupported=Storage(p)??Support(r);if(unsupported!=null)return unsupported;
        if(r.Disabled)return "recipe_disabled";
        if(!tileRequirements(p,r))return "missing_crafting_station";
        if(!environmentRequirements(p,r)||r.Conditions.Any(c=>!conditions[c]()))return "environment_not_met";
        if(!Materials(r,p))return "insufficient_materials";
        return null;
    }
    private static object ItemInfo(Item i)=>new{typeId=i.type,contentKey=i.ModItem?.FullName??"Terraria/"+ItemID.Search.GetName(i.type),name=i.Name,count=i.stack};
    internal static object ReadRecipes(AiReadOnlyProtocol.Identity id,JsonElement args)=>InScope(id,p=>
    {
        int limit=args.TryGetProperty("limit",out var n)?n.GetInt32():12,offset=args.TryGetProperty("offset",out n)?n.GetInt32():0;
        int? resultType=args.TryGetProperty("resultType",out n)?n.GetInt32():null;
        bool availableOnly=!args.TryGetProperty("availableOnly",out n)||n.GetBoolean();
        var list=new List<object>();int matched=0,unsupported=0;
        for(int index=0;index<Recipe.numRecipes;index++)
        {
            var r=Main.recipe[index];if(r.createItem.IsAir||resultType.HasValue&&r.createItem.type!=resultType)continue;
            string support=Support(r),reason=Available(r,p);if(support!=null)unsupported++;
            if(availableOnly&&reason!=null)continue;
            int position=matched++;if(position<offset||list.Count>=limit)continue;
            list.Add(new{recipeId=index,recipeToken=Token(id,index,r),output=ItemInfo(r.createItem),supported=support==null,available=reason==null,reason,
                hasOutputSpace=Fits(p,r.createItem),ingredients=r.requiredItem.Select(i=>new{item=ItemInfo(i),acceptedGroups=r.acceptedGroups.Where(g=>RecipeGroup.recipeGroups[g].ContainsItem(i.type)).Select(g=>new{id=g,name=RecipeGroup.recipeGroups[g].GetText(),typeIds=RecipeGroup.recipeGroups[g].ValidItems.OrderBy(t=>t).ToArray()}).ToArray()}).ToArray(),
                stations=r.requiredTile.Select(t=>new{typeId=t,contentKey="Terraria/"+TileID.Search.GetName(t),adjacent=p.adjTile[t]}).ToArray(),
                conditions=r.Conditions.Select(c=>new{description=c.Description.Value,supported=conditions.ContainsKey(c),met=conditions.ContainsKey(c)?(bool?)conditions[c]():null}).ToArray()});
        }
        return (object)new{items=list,matchedCount=matched,returnedCount=list.Count,offset,truncated=offset+list.Count<matched,unsupportedCount=unsupported,
            registryRecipeCount=Recipe.numRecipes,availableOnly,storageReason=Storage(p),scope="loaded native registry; supported ordinary vanilla recipes, inventory slots 0..57; native stations; six supported liquid/time conditions; no chest/void/mod storage or special callbacks",
            quantities="crafts is recipe executions, not output count; output.count per execution; space checked conservatively before consumption; recipe token scoped to binding/definition, revalidated at execution"};
    });
    internal static Context Begin(AiReadOnlyProtocol.Identity id,JsonElement args)=>new(){Identity=id,RecipeId=args.GetProperty("recipeId").GetInt32(),Token=args.GetProperty("recipeToken").GetString(),Requested=args.GetProperty("crafts").GetInt32()};
    internal static void Step(Context c)
    {
        if(c.Status!="running")return;
        InScope(c.Identity,p=>
        {
            string reason=null;
            var r=c.RecipeId>=0&&c.RecipeId<Recipe.numRecipes?Main.recipe[c.RecipeId]:null;
            if(r==null||Token(c.Identity,c.RecipeId,r)!=c.Token)reason="recipe_changed";
            else reason=Available(r,p)??(!Fits(p,r.createItem)?"insufficient_inventory_space":null);
            if(reason!=null){c.Status="failed";c.Reason=reason;return false;}
            c.OutputType=r.createItem.type;c.OutputCount=r.createItem.stack;
            var before=p.inventory.Take(58).GroupBy(i=>i.type).ToDictionary(g=>g.Key,g=>g.Sum(i=>i.stack));
            buffer=new Item();currentStep=c;c.MutationAttempted=true;
            try
            {
                craftNative(r);c.Pending=buffer;
                if(buffer.IsAir||buffer.type!=r.createItem.type||buffer.stack!=r.createItem.stack)throw new InvalidOperationException("Native output differs from recipe");
                // OnCraft clears ConsumedItems; collect the native record in Consume instead.
                var batch=JsonSerializer.SerializeToElement(c.Batches[^1]);
                var materialCounts=batch.GetProperty("consumed").EnumerateArray().GroupBy(i=>i.GetProperty("typeId").GetInt32()).ToDictionary(g=>g.Key,g=>g.Sum(i=>i.GetProperty("count").GetInt32()));
                c.Pending=p.GetItem(p.whoAmI,buffer,GetItemSettings.InventoryEntityToPlayerInventorySettings);
                if(!c.Pending.IsAir)throw new InvalidOperationException("Native output did not fully enter inventory");
                var after=p.inventory.Take(58).GroupBy(i=>i.type).ToDictionary(g=>g.Key,g=>g.Sum(i=>i.stack));
                foreach(int type in before.Keys.Concat(after.Keys).Concat(materialCounts.Keys).Distinct().Where(t=>t!=0))
                    if(after.GetValueOrDefault(type)-before.GetValueOrDefault(type)!=(type==r.createItem.type?r.createItem.stack:0)-materialCounts.GetValueOrDefault(type))
                        throw new InvalidOperationException("Native crafting inventory delta mismatch");
                c.Completed++;c.Produced+=r.createItem.stack;
                NetworkStateSync.Full(p);
                if(c.Completed==c.Requested){c.Status="completed";c.Reason="native_recipe_crafted_and_received";}
            }
            catch(Exception e){c.Status="unknown";c.Reason="native_craft_unconfirmed";ProbeSession.Quarantine("Crafting has possible partial mutation; no replay: "+e.Message);throw;}
            return true;
        });
    }
    internal static object Evidence(Context c)=>new{recipeId=c.RecipeId,recipeToken=c.Token,requestedCrafts=c.Requested,completedCrafts=c.Completed,producedCount=c.Produced,mutationAttempted=c.MutationAttempted,
        outputTypeId=c.OutputType,outputCountPerCraft=c.OutputCount,
        batches=c.Batches,pendingOutput=c.Pending==null?null:ItemInfo(c.Pending),inventoryVerified=c.Status=="completed",effectStatus=c.Status=="completed"?"native_crafted_to_inventory":"not_fully_confirmed"};
}
