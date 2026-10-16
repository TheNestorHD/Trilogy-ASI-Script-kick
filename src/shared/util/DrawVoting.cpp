#include "DrawVoting.h"

#include "util/ColorHelper.h"
#include "util/Config.h"
#include "util/Globals.h"
#include "util/TextEncoding.h"

static const float TEXT_SCALE_X = 0.6f;
static const float TEXT_SCALE_Y = 0.8f;

static const float SUBTEXT_SCALE_X = 0.4f;
static const float SUBTEXT_SCALE_Y = 0.6f;

void
DrawVoting::DrawVotes ()
{
    if (GenericUtil::IsMenuActive ()) return;

    // Always try to draw votes
    for (int i = 0; i < 3; i++)
        DrawVote (i);

    // Explicitly list every winning option when multiple effects are selected.
    // The regular vote labels remain visible and highlighted as before.
    if (drawRemaining > 0 && pickedVote != UNDETERMINED)
    {
        int selectedCount = 0;
        for (int i = 0; i < 3; i++)
        {
            if (pickedVote & GetVoteChoice (i))
                selectedCount++;
        }

        if (selectedCount > 1)
        {
            bool drawVotesOnTop = CONFIG ("Drawing.DrawVotesOnTop", false);
            float headerY = drawVotesOnTop
                                ? SCREEN_COORD_BOTTOM (105.0f)
                                : SCREEN_COORD_TOP (105.0f);
            float rowStep = SCREEN_COORD (22.0f);
            CRGBA selectedColor = color::White;

            gamefont::PrintUnscaled ("Activated effects:", SCREEN_COORD_CENTER_X,
                                     headerY, FONT_DEFAULT,
                                     SCREEN_MULTIPLIER (0.5f),
                                     SCREEN_MULTIPLIER (0.7f), selectedColor,
                                     gamefont::AlignCenter, 1, color::Black, true);

            int row = 1;
            for (int i = 0; i < 3; i++)
            {
                if (!(pickedVote & GetVoteChoice (i)))
                    continue;

                std::string description
                    = Globals::enabledEffects["replace_all_text"]
                          ? Globals::replaceAllTextString
                          : votes[i].description;
                float y = drawVotesOnTop
                              ? SCREEN_COORD_BOTTOM (105.0f) + rowStep * row
                              : SCREEN_COORD_TOP (105.0f) + rowStep * row;

                gamefont::PrintUnscaled (description, SCREEN_COORD_CENTER_X, y,
                                         FONT_DEFAULT, SCREEN_MULTIPLIER (0.5f),
                                         SCREEN_MULTIPLIER (0.7f), selectedColor,
                                         gamefont::AlignCenter, 1, color::Black, true);
                row++;
            }
        }
    }

    if (drawRemaining > 0)
    {
        // Make sure to bring back the picked one
        for (int i = 0; i < 3; i++)
        {
            if (pickedVote == UNDETERMINED)
            {
                votes[i].offset
                    = std::min (votes[i].offset
                                    + (float) GenericUtil::CalculateTick (
                                        0.0015f),
                                1.0f);
            }
        }

        drawRemaining
            = std::max (0, drawRemaining - (int) GenericUtil::CalculateTick ());
        return;
    }

    // Finally get rid of every effect again
    for (int i = 0; i < 3; i++)
    {
        votes[i].offset
            = std::max (0.0f,
                        votes[i].offset
                            - (float) GenericUtil::CalculateTick (0.0015f));
    }
}

void
DrawVoting::UpdateVotes (std::vector<std::string> effects,
                         std::vector<int> _votes, eVoteChoice _pickedVote)
{
    totalVotes = 0;
    for (int i = 0; i < 3; i++)
    {
        votes[i] = VotingElement (TextEncoding::ToGameText (effects[i]),
                                  _votes[i], votes[i].offset);

        if (votes[i].votes > 0) totalVotes += votes[i].votes;
    }

    pickedVote = _pickedVote;

    drawRemaining = 15000;
}

void
DrawVoting::DrawVote (int choice)
{
    if (!drawRemaining && votes[choice].offset == 0.0f) return;
    if (votes[choice].description == "") return;

    float barX = 0.0f;
    float x    = 0.0f;
    if (choice == 0)
    { // Left Align
        x = SCREEN_COORD_CENTER_X - SCREEN_COORD (400.0f);
    }
    else if (choice == 1)
    { // Center Align
        x = SCREEN_COORD_CENTER_X;
    }
    else if (choice == 2)
    { // Right Align
        x = SCREEN_COORD_CENTER_X + SCREEN_COORD (400.0f);
    }

    bool drawVotesOnTop = CONFIG ("Drawing.DrawVotesOnTop", false);

#ifdef GTASA
    float y = SCREEN_COORD_BOTTOM (CalculateYOffset (choice, 75.0f));

    if (drawVotesOnTop)
        y = SCREEN_COORD_TOP (CalculateYOffset (choice, 75.0f))
            - SCREEN_MULTIPLIER (TEXT_SCALE_Y);
#else
    float y = SCREEN_COORD_BOTTOM (CalculateYOffset (choice, 85.0f));

    if (drawVotesOnTop)
        y = SCREEN_COORD_TOP (CalculateYOffset (choice, 85.0f))
            - SCREEN_MULTIPLIER (TEXT_SCALE_Y);
#endif
    std::string_view description = votes[choice].description;

    if (Globals::enabledEffects["replace_all_text"]
        && pickedVote != UNDETERMINED)
        description = Globals::replaceAllTextString;

    CRGBA color = pickedVote == UNDETERMINED ? color::White : color::DarkGray;

    if (pickedVote & GetVoteChoice (choice)) color = color::White;

    gamefont::PrintUnscaled (std::string (description), x, y, FONT_DEFAULT,
                             SCREEN_MULTIPLIER (TEXT_SCALE_X),
                             SCREEN_MULTIPLIER (TEXT_SCALE_Y), color,
                             gamefont::AlignCenter, 1, color::Black, true);

    // Vote Bars
    float barWidth = 200.0f;
    float barStart = x - SCREEN_COORD (barWidth / 2);
    y += drawVotesOnTop ? -SCREEN_COORD (20.0f) : SCREEN_COORD (30.0f);

    // Outline Bar
    CRect rect
        = CRect (barStart - SCREEN_COORD (2.0f), y - SCREEN_COORD (2.0f),
                 barStart + SCREEN_COORD (barWidth) + SCREEN_COORD (2.0f),
                 y + SCREEN_COORD (18.0f) + SCREEN_COORD (2.0f));
    CSprite2d::DrawRect (rect, color::Black);

    // Background Bar
    rect = CRect (barStart, y, barStart + SCREEN_COORD (barWidth),
                  y + SCREEN_COORD (16.0f));
    CSprite2d::DrawRect (rect, ColorHelper::GetBackgroundColor ());

    // Filled Bar
    rect
        = CRect (barStart, y,
                 barStart + SCREEN_COORD (CalculateBarWidth (choice, barWidth)),
                 y + SCREEN_COORD (16.0f));
    CSprite2d::DrawRect (rect, ColorHelper::GetForegroundColor ());

    // Draw Percentage
    float percentage_x = barStart + 150.0f;

    gamefont::PrintUnscaled (GetPercentage (choice), x, y - SCREEN_COORD (3.0f),
                             FONT_DEFAULT, SCREEN_MULTIPLIER (SUBTEXT_SCALE_X),
                             SCREEN_MULTIPLIER (SUBTEXT_SCALE_Y), color,
                             gamefont::AlignCenter, 1, color::Black, true);
}

float
DrawVoting::CalculateYOffset (int choice, float adjustment)
{
    if (choice < 0 || choice > 2) return 0.0f;

    float t            = votes[choice].offset;
    float easePosition = 1 + (--t) * t * (2.70158f * t + 1.70158f);
    return easePosition * adjustment;
}

float
DrawVoting::CalculateBarWidth (int choice, float maxWidth)
{
    if (totalVotes == 0 || votes[choice].votes == -1) return 0.0f;

    return maxWidth * ((float) votes[choice].votes / (float) totalVotes);
}

std::string
DrawVoting::GetPercentage (int choice)
{
    if (Globals::enabledEffects["replace_all_text"])
        return Globals::replaceAllTextString;

    std::string percentage;

    if (votes[choice].votes > -1)
    {
        int i_percentage = totalVotes == 0
                               ? 0
                               : (int) round ((float) votes[choice].votes
                                              / (float) totalVotes * 100.0f);

        percentage.append (std::to_string (i_percentage));
        percentage.append ("%");
        percentage.append (" - ");
        percentage.append (std::to_string (votes[choice].votes));
        percentage.append ("/");
        percentage.append (std::to_string (totalVotes));
    }
    else
    {
        percentage.append ("?%");
    }

    return percentage;
}
