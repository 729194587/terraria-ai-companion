namespace CompanionBridge.ServerIpc;

internal sealed record ExcavationProof(int X,int Y,int Type,int Update,int Strikes);
internal sealed record MiningRunReceipt(int requested,ExcavationProof[] verified,float estimatedUpdates,int actualUpdates,int strikes,int toolType,float? workLeftX=null,float? workFeetY=null);
