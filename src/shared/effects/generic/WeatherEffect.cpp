#include "util/EffectBase.h"

#include <CWeather.h>

class WeatherEffect : public EffectBase
{
    eWeatherType oldWeather = eWeatherType::WEATHER_EXTRASUNNY;

    eWeatherType weather = eWeatherType::WEATHER_EXTRASUNNY;

public:
    void
    OnStart (EffectInstance *inst) override
    {
        this->oldWeather = static_cast<eWeatherType> (CWeather::OldWeatherType);
        this->weather = static_cast<eWeatherType> (
            inst->GetCustomData ().value ("weatherID", 0));
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
