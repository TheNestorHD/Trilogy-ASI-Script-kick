#include "util/EffectBase.h"
#include "util/EffectInstance.h"
#include "util/GameUtil.h"

#include <algorithm>

#include <CWorld.h>

#include <CStreaming.h>

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
            CWorld::Remove (entity);
            CWorld::Add (entity);

            CStreaming::StreamZoneModels (destination);
        }

        inst->Disable ();
    }
};

DEFINE_EFFECT (TeleportEffect, "effect_teleport", GROUP_TELEPORT);
