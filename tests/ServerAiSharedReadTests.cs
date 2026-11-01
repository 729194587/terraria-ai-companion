using System;
using System.Linq;
using System.Text.Json;
using CompanionBridge;
using CompanionBridge.Common.Systems;
using Terraria;
using Terraria.GameContent;

// Actual shared inventory projection, with native storage/content substitutes.
public static class ServerAiSharedReadTests
{
    public static void Run()
    {
        int checks=0;void Check(bool ok,string text){if(!ok)throw new Exception(text);checks++;Console.WriteLine("PASS shared AI inventory fixture: "+text);}
        var p=new Player{selectedItem=9,inventory=Enumerable.Range(0,59).Select(_=>new Item{type=0,stack=0}).ToArray()};
        p.inventory[9]=new Item{type=2,stack=5,pick=35,prefix=3,maxStack=9999,favorited=true,DisplayName="fixture pick"};
        p.inventory[50]=new Item{type=2,stack=7};
        JsonElement Query(object args)=>JsonSerializer.SerializeToElement(AtomicOperations.ReadInventory(p,JsonSerializer.SerializeToElement(args),(_,_)=>null,()=>throw new Exception("compact mode must not build full snapshot")));
        bool missing=false;try{Query(new{});}catch(FormatException){missing=true;}Check(missing,"missing inventory mode is rejected before reading");
        var selected=Query(new{mode="selected"});var item=selected.GetProperty("inventory")[0];
        int[] Inspected(JsonElement value)=>value.GetProperty("inspectedSlots").EnumerateArray().Select(v=>v.GetInt32()).ToArray();
        Check(Inspected(selected).SequenceEqual(new[]{9})&&selected.GetProperty("inventoryScope").GetProperty("mode").GetString()=="selected","selected reports only actual internal slot");
        Check(Inspected(Query(new{mode="hotbar"})).SequenceEqual(Enumerable.Range(0,10)),"hotbar reports all ten inspected slots");
        Check(Inspected(Query(new{mode="slots",slots=new[]{58,9,50,0}})).SequenceEqual(new[]{58,9,50,0}),"slots scope preserves exact requested indices");
        Check(selected.GetProperty("selectedSlot").GetInt32()==9&&selected.GetProperty("selectedHotbarKey").GetInt32()==0,"selected inventory index and public key stay distinct");
        Check(item.GetProperty("use").GetProperty("pick").GetInt32()==35&&item.GetProperty("prefix").GetInt32()==3&&item.GetProperty("stack").GetInt32()==5,"native tool power, prefix and stack are copied");
        Check(item.GetProperty("inventoryToken").ValueKind==JsonValueKind.Null,"server supplies no human-scoped inventory token");
        Check(Query(new{mode="hotbar"}).GetProperty("inventory").GetArrayLength()==10,"hotbar has native indices 0..9");
        var slots=Query(new{mode="slots",slots=new[]{58,9,50,0}}).GetProperty("inventory");
        Check(slots.EnumerateArray().Select(i=>i.GetProperty("slot").GetInt32()).SequenceEqual(new[]{58,9,50,0}),"explicit slot order including 58 is preserved");
        Check(slots[0].GetProperty("empty").GetBoolean()&&slots[0].GetProperty("stack").GetInt32()==0,"empty native slot remains explicitly empty");
        var registry=ContentSamples.ItemsByType.ToArray();
        try{
            ContentSamples.ItemsByType.Clear();ContentSamples.ItemsByType.Add(2,new Item{type=2,stack=1,DisplayName="fixture pick"});
            var counted=Query(new{mode="item",item="fixture pick"});
            Check(Inspected(counted).SequenceEqual(Enumerable.Range(0,59)),"item searches all inventory slots, not just its two matching positions");
            Check(Inspected(Query(new{mode="item",item="missing"})).Length==0,"unresolved content does not claim inventory search");
            Check(counted.GetProperty("count").GetInt32()==12&&counted.GetProperty("slots").GetArrayLength()==2,"item resolution counts only matching actual inventory slots");
            Check(Query(new{mode="item",item="missing"}).GetProperty("status").GetString()=="unknown_item","missing item name is not zero-count resolved content");
            ContentSamples.ItemsByType.Add(3,new Item{type=3,stack=1,DisplayName="fixture pick"});
            Check(Query(new{mode="item",item="fixture pick"}).GetProperty("status").GetString()=="ambiguous_item","ambiguous localized names are not guessed");
        }finally{ContentSamples.ItemsByType.Clear();foreach(var pair in registry)ContentSamples.ItemsByType.Add(pair.Key,pair.Value);}
        Check(p.selectedItem==9&&p.inventory[9].stack==5&&p.inventory[50].stack==7,"queries never select or mutate inventory contents");
        Console.WriteLine($"Shared AI inventory fixture: {checks} checks; no native game.");
    }
}
