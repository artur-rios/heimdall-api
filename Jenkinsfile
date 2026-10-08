// Deployment pipeline, run by the self-hosted Jenkins in yggdrasil (github.com/artur-rios/yggdrasil).
//
// All the logic lives in that repository's shared library so the four applications deploy the same
// way. What it does with this repository:
//
//   develop pushed                       -> deploy to development (left stopped if it was stopped:
//                                           it is on demand, scripts/ygg.sh env start development)
//   release/x.y.z pushed                 -> deploy to homologation (on demand, likewise)
//   pull request release/x.y.z -> main   -> wait for every GitHub check to pass, deploy to
//                                           production, merge the pull request, tag vx.y.z,
//                                           delete the release branch
//
// Development, homologation and production share one VPS; local (Docker Desktop) is deployed by
// hand and never from here.
//
// Build and test stay in GitHub Actions; this file only deploys.

@Library('yggdrasil') _

yggdrasilPipeline(stack: 'heimdall-api')
