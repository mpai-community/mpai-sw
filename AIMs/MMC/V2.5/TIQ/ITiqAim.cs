using System.Threading.Tasks;
using Mpai.Core;

namespace Mpai.Mmc.Tiq;

// MMC-TIQ contract: answer a question (Basic Text Object) about an image
// (Basic Visual Object), returning the answer as a Basic Text Object.
public interface ITiqAim
{
    Task<BasicTextObject> ProcessAsync(BasicTextObject question, BasicVisualObject image);
}
