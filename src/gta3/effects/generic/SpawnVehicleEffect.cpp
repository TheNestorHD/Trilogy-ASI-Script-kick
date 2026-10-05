#include "util/EffectBase.h"
#include "util/EffectInstance.h"

#include <algorithm>

#include <CStreaming.h>
#include <CTheScripts.h>
#include <extensions/ScriptCommands.h>

using namespace plugin;

class SpawnVehicleEffect : public EffectBase
{
public:
    bool
    CanActivate () override
    {
        CPlayerPed *player = FindPlayerPed ();
        return player && player->m_nAreaCode == 0;
    }

    void
    OnStart (EffectInstance *inst) override
    {
        inst->SetIsOneTimeEffect ();
    }

    void
    OnTick (EffectInstance *inst) override
    {
        if (!CanActivate ())
        {
            inst->ResetTimer ();
            return;
        }

        CPlayerPed *player = FindPlayerPed ();
        if (!player) return;

        int vehicleID = inst->GetCustomData ().value ("vehicleID", 90);
        vehicleID = std::clamp (vehicleID, 90, 150);

        CVector position = player->TransformFromObjectSpace (CVector (0.0f, 5.0f, 0.0f));

        CStreaming::RequestModel (vehicleID, 1);
        CStreaming::LoadAllRequestedModels (false);

        CVehicle *vehicle = nullptr;
        if (CStreaming::ms_aInfoForModel[vehicleID].m_nLoadState
            == LOADSTATE_LOADED)
        {
            CStreaming::SetModelIsDeletable (vehicleID);
            Command<eScriptCommands::COMMAND_CREATE_CAR> (
                vehicleID, position.x, position.y, position.z, &vehicle);
        }

        if (vehicle)
        {
            CTheScripts::ClearSpaceForMissionEntity (position, vehicle);
        }

        inst->Disable ();
    }
};

DEFINE_EFFECT (SpawnVehicleEffect, "effect_spawn_vehicle", 0);
