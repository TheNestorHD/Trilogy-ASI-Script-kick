#pragma once

#include <CMenuManager.h>
#include <CCutsceneMgr.h>
#include <CPlayerPed.h>

class GameUtil
{
public:
    static bool
    IsCutsceneProcessing ()
    {
        return CCutsceneMgr::ms_running || CCutsceneMgr::ms_cutsceneProcessing;
    }

    static bool
    IsPlayerSafe ()
    {
        CPlayerPed *player = FindPlayerPed ();
        if (!player || !player->CanSetPedState ()) return false;

        switch (player->m_ePedState)
        {
            case PEDSTATE_ARRESTED:
            case PEDSTATE_DEAD:
            case PEDSTATE_DIE:
                return false;
            default:
                break;
        }

        if (FrontEndMenuManager.m_bMenuActive) return false;
        if (IsCutsceneProcessing ()) return false;

        return player->m_bCanBeDamaged;
    }
};
