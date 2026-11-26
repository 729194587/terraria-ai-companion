namespace CompanionBridge.ServerIpc;

internal sealed partial record ExcavationCell(int X,int Y,int Type,string Shape,long Revision);
internal sealed record ExcavationProof(int X,int Y,int Type,int Update,int Strikes,System.Guid? ActionId=null);
internal sealed record VeinBreakProof(ExcavationCell Before,long AfterRevision,int SeedX,int SeedY,int ActorSlot,System.Guid ActionId,int Update);
internal sealed record MiningRunReceipt(int requested,ExcavationProof[] verified,float estimatedUpdates,int actualUpdates,int strikes,int toolType,float? workLeftX=null,float? workFeetY=null,VeinBreakProof[] chainBreaks=null);
