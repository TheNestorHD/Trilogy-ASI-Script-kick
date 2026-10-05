#include "util/EffectBase.h"
#include "util/EffectInstance.h"
#include "util/GameUtil.h"

#include <algorithm>

#include <CWorld.h>

#include <CGame.h>
#include <CStreaming.h>
#include <CTimeCycle.h>

class TeleportEffect : public EffectBase
{
public:
    bool
    CanActivate () override
    {
        return GameUtil::IsPlayerSafe ();
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

        CVector destination = {
            inst->GetCustomData ().value ("posX", 0.0f),
            inst->GetCustomData ().value ("posY", 0.0f),
            inst->GetCustomData ().value ("posZ", 0.0f)
        };

        CEntity *entity = FindPlayerEntity ();
        if (entity)
        {
            entity->Teleport (destination);
            CGame::currArea = 0;
            entity->m_nAreaCode = 0;

            CStreaming::RemoveBuildingsNotInArea (0);
            CWorld::Remove (entity);
            CWorld::Add (entity);

            CStreaming::StreamZoneModels (&destination);
            CTimeCycle::StopExtraColour (false);
        }

        inst->Disable ();
    }
};

DEFINE_EFFECT (TeleportEffect, "effect_teleport", GROUP_TELEPORT);
