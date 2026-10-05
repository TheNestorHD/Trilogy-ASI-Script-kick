#include "util/EffectBase.h"

#include <CWeather.h>

class WeatherEffect : public EffectBase
{
    short oldWeather = 0;

    short weather = 0;

public:
    void
    OnStart (EffectInstance *inst) override
    {
        this->oldWeather = CWeather::OldWeatherType;
        this->weather = static_cast<short> (inst->GetCustomData ().value ("weatherID", 0));
    }

    void
    OnEnd (EffectInstance *) override
    {
        CWeather::ForceWeatherNow (this->oldWeather);
    }

    void
    OnTick (EffectInstance *) override
    {
        CWeather::ForceWeatherNow (this->weather);
    }
};

DEFINE_EFFECT (WeatherEffect, "effect_weather", GROUP_WEATHER);
